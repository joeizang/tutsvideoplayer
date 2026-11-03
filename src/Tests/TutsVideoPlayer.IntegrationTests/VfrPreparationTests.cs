using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;
using TutsVideoPlayer.Web.Features.Preparation;

namespace TutsVideoPlayer.IntegrationTests;

public sealed class VfrPreparationTests : IAsyncLifetime
{
    private readonly string root = Directory.CreateTempSubdirectory("tuts-vfr-").FullName;
    private AppDbContext db = null!;
    private LibraryEntity library = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly FFprobeAdapter probe = new("ffprobe");

    public async ValueTask InitializeAsync()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "test.db")}").Options);
        await db.Database.MigrateAsync(Token);
        library = new LibraryEntity { LogicalIdentity = "vfr-fixture" };
        db.Libraries.Add(library);
        await db.SaveChangesAsync(Token);
        Directory.CreateDirectory(Path.Combine(root, "Course"));
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    private async Task<string> CreateSourceAsync(string name = "variable.mp4", string? rate = null)
    {
        var path = Path.Combine(root, "Course", name);
        var args = new List<string> { "-f", "lavfi", "-i", $"testsrc2=duration=2:size=640x600:rate={rate ?? "60"}",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2" };
        if (rate is null)
            args.AddRange(["-vf", "select='not(mod(n,2))+not(mod(n,11))'", "-fps_mode", "vfr"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", path]);
        await FFmpegAsync(args);
        return path;
    }

    private static async Task<string> FFmpegAsync(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-y" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        var output = await error;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private async Task ScanAsync()
    {
        var run = new ScanRunEntity { State = "Running" };
        db.ScanRuns.Add(run);
        await db.SaveChangesAsync(Token);
        var scanner = new LibraryScanner(db, new FileSystemLibraryEnumerator(), probe, NullLogger<LibraryScanner>.Instance);
        var outcome = await scanner.ExecuteAsync(run.Id, library, root, Token);
        Assert.True(outcome.Succeeded);
        db.ChangeTracker.Clear();
    }

    private PreparationExecutor Executor()
    {
        var options = Options.Create(new AppOptions { LibraryRoot = root, DataDirectory = root });
        return new PreparationExecutor(db, options, Options.Create(new PreparationOptions { DiskReserveBytes = 0 }),
            probe, new FFmpegAdapter("ffmpeg"), new PreparedOutputValidator(probe),
            new CacheAccounting(db, options), new CacheEvictionService(db, options, NullLogger<CacheEvictionService>.Instance),
            NullLogger<PreparationExecutor>.Instance);
    }

    [Theory]
    [InlineData("15")]
    [InlineData("30000/1001")]
    public async Task CfrSourcesStayReadyAndAreNotScheduled(string rate)
    {
        var path = await CreateSourceAsync(rate: rate);
        var result = await probe.ProbeAsync(path, Token);
        Assert.True(result.Success);
        Assert.False(result.IsVariableFrameRate);
        Assert.NotNull(result.NominalFrameRate);
        Assert.NotNull(result.AverageFrameRate);
        await ScanAsync();
        Assert.Equal(RenditionStatus.Ready, (await db.Renditions.SingleAsync(Token)).Status);
        Assert.Empty(await db.PreparationJobs.ToListAsync(Token));
    }

    [Fact]
    public async Task VfrMp4IsNormalizedBeforePlaybackWithoutChangingSourceBytes()
    {
        var source = await CreateSourceAsync();
        var sourceHash = PreparationArtifacts.Hash(source);
        Assert.True((await probe.ProbeAsync(source, Token)).IsVariableFrameRate);
        await ScanAsync();
        var job = await db.PreparationJobs.SingleAsync(Token);
        Assert.Equal(PreparationRecipes.NormalizationVersion, job.RecipeVersion);
        Assert.Equal(RenditionStatus.Pending, (await db.Renditions.SingleAsync(Token)).Status);
        var result = await Executor().ExecuteAsync(job.Id, Token);
        Assert.Equal(PreparationJobState.Succeeded, result.FinalState);
        var output = await db.Renditions.SingleAsync(r => r.Purpose == RenditionPurpose.Compatibility, Token);
        await AssertNormalizedAsync(Path.Combine(root, output.RelativePath));
        Assert.Equal(sourceHash, PreparationArtifacts.Hash(source));
        await ScanAsync();
        Assert.Single(await db.PreparationJobs.ToListAsync(Token));
        Assert.Equal(PreparationJobState.Succeeded, (await db.PreparationJobs.SingleAsync(Token)).State);
    }

    [Fact]
    public async Task LegacyProbeMetadataIsRefreshedWithoutChangingSourceGeneration()
    {
        await CreateSourceAsync();
        await ScanAsync();
        var lesson = await db.Lessons.Include(l => l.SourceComponents).SingleAsync(Token);
        var generation = lesson.SourceGeneration;
        lesson.SourceComponents.Single().ProbeMetadata = "{\"DurationMs\":2000,\"VideoCodec\":\"h264\",\"AudioCodec\":\"aac\",\"Width\":640,\"Height\":600}";
        await db.SaveChangesAsync(Token);
        await ScanAsync();
        lesson = await db.Lessons.Include(l => l.SourceComponents).SingleAsync(Token);
        Assert.Equal(generation, lesson.SourceGeneration);
        Assert.True(LibraryScanner.ParseProbeMetadata(lesson.SourceComponents.Single().ProbeMetadata)!.IsVariableFrameRate);
        Assert.Equal(RenditionStatus.Pending, (await db.Renditions.SingleAsync(Token)).Status);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task ScanRequeuesOldCopiesAndExecutorDoesNotAdoptThem(bool quality, bool leased, bool alreadyStale)
    {
        var source = await CreateSourceAsync();
        await ScanAsync();
        var lesson = await db.Lessons.SingleAsync(Token);
        var compatibilityJob = await db.PreparationJobs.SingleAsync(Token);
        var fingerprint = SourceFingerprint.Compute([("video", lesson.PrimaryRelativePath, source)]);
        string outputPath;
        PreparationJobEntity oldJob;
        if (quality)
        {
            await QualityScheduling.QueueQualityForLessonAsync(db, lesson.Id, "480", Token);
            oldJob = await db.PreparationJobs.SingleAsync(j => j.Purpose == RenditionPurpose.Quality, Token);
            outputPath = Path.Combine(root, ".tutsvideoplayer", "quality", lesson.Id.ToString(), "1", $"variable-480-{fingerprint[..12]}.mp4");
        }
        else
        {
            oldJob = compatibilityJob;
            outputPath = Path.Combine(root, "Course", PreparationExecutor.OutputFileNameFor(lesson.PrimaryRelativePath, fingerprint));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await FFmpegAsync(["-i", source, "-c", "copy", outputPath]);
        oldJob.State = PreparationJobState.Succeeded;
        oldJob.Attempt = 3;
        oldJob.RecipeVersion = quality ? "quality-480-v1" : PreparationRecipes.RemuxVersion;
        var relative = Path.GetRelativePath(root, outputPath).Replace('\\', '/');
        new PreparationManifest { JobId = oldJob.Id, LessonId = lesson.Id, SourceGeneration = 1,
            RecipeVersion = oldJob.RecipeVersion, OutputFileName = Path.GetFileName(outputPath),
            OutputHash = PreparationArtifacts.Hash(outputPath), OutputLengthBytes = new FileInfo(outputPath).Length,
            Sources = [new ManifestSource("video", lesson.PrimaryRelativePath, fingerprint)] }
            .WriteAtomically(outputPath + ".tvp.json");
        db.Renditions.Add(new RenditionEntity { LessonId = lesson.Id, SourceGeneration = 1,
            Purpose = oldJob.Purpose, RetentionClass = quality ? RenditionRetention.Quality : RenditionRetention.Permanent,
            Profile = quality ? "480" : null, Status = alreadyStale ? RenditionStatus.Stale : RenditionStatus.Ready,
            RecipeVersion = oldJob.RecipeVersion,
            RelativePath = relative, ManifestPath = relative + ".tvp.json", ByteLength = new FileInfo(outputPath).Length });
        await db.SaveChangesAsync(Token);
        await ScanAsync();
        oldJob = await db.PreparationJobs.SingleAsync(j => j.Id == oldJob.Id, Token);
        Assert.Equal(PreparationJobState.Queued, oldJob.State);
        Assert.Equal(0, oldJob.Attempt);
        Assert.Equal(RenditionStatus.Stale, (await db.Renditions.SingleAsync(r => r.Purpose == oldJob.Purpose, Token)).Status);
        oldJob.State = PreparationJobState.Running; // Simulate the worker claiming the job.
        oldJob.Attempt++;
        await db.SaveChangesAsync(Token);
        if (leased)
        {
            var stale = await db.Renditions.SingleAsync(r => r.Purpose == RenditionPurpose.Quality, Token);
            var session = new PlaybackSessionEntity { LessonId = lesson.Id, SourceGeneration = 1,
                ActiveRenditionId = stale.Id, LastHeartbeatUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            db.PlaybackSessions.Add(session);
            await db.SaveChangesAsync(Token);
            var deferred = await Executor().ExecuteAsync(oldJob.Id, Token);
            Assert.Equal(PreparationJobState.Blocked, deferred.FinalState);
            Assert.Equal("outdated-copy-in-use", deferred.ErrorCode);
            Assert.True(File.Exists(outputPath));
            session.ClosedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            oldJob.State = PreparationJobState.Running; // The learner retries and the worker claims again.
            await db.SaveChangesAsync(Token);
        }
        var outcome = await Executor().ExecuteAsync(oldJob.Id, Token);
        Assert.True(outcome.FinalState == PreparationJobState.Succeeded, $"{outcome.FinalState}: {outcome.ErrorCode}: {outcome.UserMessage}");
        var copy = await db.Renditions.SingleAsync(r => r.Purpose == oldJob.Purpose, Token);
        Assert.Equal(RenditionStatus.Ready, copy.Status);
        await AssertNormalizedAsync(Path.Combine(root, copy.RelativePath));
        if (quality) Assert.Equal(480, copy.Height);
        else Assert.True(File.Exists(outputPath)); // Permanent copies are never deleted.
        await ScanAsync(); // Compatibility and quality jobs may coexist without duplicate keys.
    }

    [Fact]
    public async Task OldCfrQualityCopyRemainsReadyWithoutRegeneration()
    {
        await CreateSourceAsync(rate: "15");
        await ScanAsync();
        var lesson = await db.Lessons.SingleAsync(Token);
        await QualityScheduling.QueueQualityForLessonAsync(db, lesson.Id, "480", Token);
        var job = await db.PreparationJobs.SingleAsync(Token);
        job.State = PreparationJobState.Running;
        job.Attempt = 1;
        await db.SaveChangesAsync(Token);
        Assert.Equal(PreparationJobState.Succeeded, (await Executor().ExecuteAsync(job.Id, Token)).FinalState);
        var copy = await db.Renditions.SingleAsync(r => r.Purpose == RenditionPurpose.Quality, Token);
        copy.RecipeVersion = job.RecipeVersion = "quality-480-v1";
        var hash = PreparationArtifacts.Hash(Path.Combine(root, copy.RelativePath));
        await db.SaveChangesAsync(Token);
        await ScanAsync();
        copy = await db.Renditions.SingleAsync(r => r.Purpose == RenditionPurpose.Quality, Token);
        Assert.Equal(RenditionStatus.Ready, copy.Status);
        Assert.Equal("quality-480-v1", copy.RecipeVersion);
        Assert.Equal(PreparationJobState.Succeeded, (await db.PreparationJobs.SingleAsync(Token)).State);
        Assert.True((await QualityScheduling.QueueQualityForLessonAsync(db, lesson.Id, "480", Token)).AlreadyReady);
        Assert.Equal(hash, PreparationArtifacts.Hash(Path.Combine(root, copy.RelativePath)));
    }

    [Theory]
    [InlineData("60000/1001", "268614000/8962921", true, "30000/1001")]
    [InlineData("15/1", "15/1", false, "15/1")]
    [InlineData("0/0", "30/1", false, "30/1")]
    [InlineData("0/0", "0/0", true, "30")]
    public void EncoderChoosesSourceAppropriateRate(string nominal, string average, bool variable, string expected)
    {
        var metadata = new ProbeMetadata(1000, "h264", "aac", 640, 600, nominal, average, variable);
        Assert.Equal(new[] { "-fps_mode", "cfr", "-r", expected }, FrameRatePolicy.EncoderArguments(metadata));
    }

    [Fact]
    public async Task ValidatorRejectsVfrWhenCfrIsRequired()
    {
        var source = await CreateSourceAsync();
        var result = await new PreparedOutputValidator(probe).ValidateAsync(source, 2000, "h264", "aac", Token,
            requireConstantFrameRate: true);
        Assert.False(result.Success);
        Assert.Contains("irregular", result.Error);
    }

    private async Task AssertNormalizedAsync(string path)
    {
        var result = await probe.ProbeAsync(path, Token);
        Assert.True(result.Success, result.Error);
        Assert.False(result.IsVariableFrameRate);
        Assert.Equal(result.NominalFrameRate, result.AverageFrameRate);
        Assert.InRange(result.DurationMs!.Value, 1900, 2200);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        Assert.True(bytes.AsSpan().IndexOf("moov"u8) < bytes.AsSpan().IndexOf("mdat"u8));
        Assert.Empty(await FFmpegAsync(["-xerror", "-i", path, "-f", "null", "-"]));
        var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "a:0", "-show_packets", "-show_entries",
            "packet=pts_time,duration_time", "-of", "json", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var json = process.StandardOutput.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        Assert.Equal(0, process.ExitCode);
        using var audio = JsonDocument.Parse(await json);
        var packets = audio.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        Assert.NotEmpty(packets);
        static double Seconds(JsonElement packet, string field) =>
            double.Parse(packet.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);
        foreach (var (previous, next) in packets.Zip(packets.Skip(1)))
            Assert.InRange(Math.Abs(Seconds(next, "pts_time") - Seconds(previous, "pts_time") - Seconds(previous, "duration_time")), 0, 0.00001);
        Assert.InRange(Math.Abs(Seconds(packets[^1], "pts_time") + Seconds(packets[^1], "duration_time")
            - result.DurationMs.Value / 1000.0), 0, 0.1);
    }

    [Fact]
    public void TimestampClassificationAllowsRoundingAndReorderedPackets()
    {
        Assert.False(FrameRatePolicy.HasIrregularTiming([0.1001, 0, 0.066733, 0.033367]));
        Assert.True(FrameRatePolicy.HasIrregularTiming([0, 0.016683, 0.05005, 0.15015]));
        Assert.False(FrameRatePolicy.HasIrregularTiming([]));
    }
}
