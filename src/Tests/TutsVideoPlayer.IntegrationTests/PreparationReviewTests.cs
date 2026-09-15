using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
using TutsVideoPlayer.Web.Hosting;

namespace TutsVideoPlayer.IntegrationTests;

public sealed class PreparationReviewTests : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string root = Directory.CreateTempSubdirectory("tuts-preparation-review-").FullName;
    private AppDbContext db = null!;
    private LibraryEntity library = null!;
    private LessonEntity lesson = null!;
    private PreparationJobEntity job = null!;
    private string Source => Path.Combine(root, "Course", "video.wmv");
    private DbContextOptions<AppDbContext> DbOptions => new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={Path.Combine(root, "test.db")}").Options;
    private AppOptions App => new() { LibraryRoot = root, DataDirectory = root };

    public async ValueTask InitializeAsync()
    {
        db = new AppDbContext(DbOptions);
        await db.Database.MigrateAsync(Token);
        library = new LibraryEntity { LogicalIdentity = "fixture" };
        db.Libraries.Add(library); await db.SaveChangesAsync(Token);
        var course = new CourseEntity { LibraryId = library.Id, RelativeDirectory = "Course" };
        db.Courses.Add(course); await db.SaveChangesAsync(Token);
        Directory.CreateDirectory(Path.Combine(root, "Course"));
        var fixture = FindFixture();
        File.Copy(fixture, Source);
        lesson = new LessonEntity { CourseId = course.Id, PrimaryRelativePath = "Course/video.wmv", DurationMs = 10000 };
        lesson.SourceComponents.Add(new SourceComponentEntity { Generation = 1, Role = SourceComponentRole.Video,
            RelativePath = lesson.PrimaryRelativePath, ProbeMetadata = "{\"VideoCodec\":\"h264\",\"AudioCodec\":\"aac\",\"DurationMs\":10000}" });
        db.Lessons.Add(lesson); await db.SaveChangesAsync(Token);
        job = new PreparationJobEntity { LessonId = lesson.Id, SourceGeneration = 1,
            DedupKey = PreparationScheduler.DedupKeyFor(lesson.Id, 1), RecipeVersion = PreparationRecipes.WmvVersion,
            State = PreparationJobState.Running, Attempt = 1 };
        db.PreparationJobs.Add(job); await db.SaveChangesAsync(Token);
    }

    private static string FindFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src/TutsVideoPlayer.Web/Media/demo-fixture.mp4");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Video fixture not found");
    }

    private PreparationExecutor Executor(long reserve = 0, string? ffmpeg = null) => new(db, Options.Create(App),
        Options.Create(new PreparationOptions { DiskReserveBytes = reserve }), new FFprobeAdapter("ffprobe"),
        new FFmpegAdapter(ffmpeg ?? "ffmpeg"), new PreparedOutputValidator(new FFprobeAdapter("ffprobe")),
        NullLogger<PreparationExecutor>.Instance);

    private async Task<string> OutputAsync(bool manifestPresent = true, bool corruptHash = false)
    {
        var fingerprint = SourceFingerprint.Compute([("video", lesson.PrimaryRelativePath, Source)]);
        var name = PreparationExecutor.OutputFileNameFor(lesson.PrimaryRelativePath, fingerprint);
        var output = Path.Combine(root, "Course", name);
        File.Copy(Source, output);
        job.OutputRelativePath = "Course/" + name;
        job.ManifestRelativePath = job.OutputRelativePath + ".tvp.json";
        if (manifestPresent)
            new PreparationManifest { JobId = job.Id, LessonId = lesson.Id, SourceGeneration = 1,
                RecipeVersion = job.RecipeVersion, OutputFileName = name, State = "Prepared",
                OutputHash = corruptHash ? new string('0', 64) : PreparationArtifacts.Hash(output),
                OutputLengthBytes = new FileInfo(output).Length, VideoCodec = "h264", AudioCodec = "aac",
                Sources = [new ManifestSource("video", lesson.PrimaryRelativePath, fingerprint)] }
                .WriteAtomically(output + ".tvp.json");
        await db.SaveChangesAsync(Token);
        return output;
    }

    [Theory]
    [InlineData(PreparationJobState.Running)]
    [InlineData(PreparationJobState.Validating)]
    [InlineData(PreparationJobState.Publishing)]
    [InlineData(PreparationJobState.Interrupted)]
    public async Task RecoveryRequeuesEveryUnfinishedState(PreparationJobState state)
    {
        job.State = state; await db.SaveChangesAsync(Token);
        await Executor().RecoverInterruptedAsync(Token);
        Assert.Equal(PreparationJobState.Queued, job.State);
    }

    [Theory]
    [InlineData(PreparationJobState.Running)]
    [InlineData(PreparationJobState.Validating)]
    [InlineData(PreparationJobState.Publishing)]
    [InlineData(PreparationJobState.Interrupted)]
    public async Task RecoveryAdoptsVerifiedPublication(PreparationJobState state)
    {
        job.State = state; await OutputAsync();
        await Executor().RecoverInterruptedAsync(Token);
        Assert.Equal(PreparationJobState.Succeeded, job.State);
        Assert.Equal(RenditionStatus.Ready, (await db.Renditions.SingleAsync(Token)).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AdoptionRejectsMissingOrWrongProvenance(bool present, bool corrupt)
    {
        var output = await OutputAsync(present, corrupt);
        var before = File.Exists(output + ".tvp.json") ? File.ReadAllBytes(output + ".tvp.json") : null;
        var result = await Executor().ExecuteAsync(job.Id, Token);
        Assert.Equal(PreparationJobState.Failed, result.FinalState);
        Assert.Empty(await db.Renditions.ToListAsync(Token));
        Assert.True(File.Exists(output));
        if (before is not null) Assert.Equal(before, File.ReadAllBytes(output + ".tvp.json"));
    }

    [Fact]
    public async Task ExistingVerifiedOutputCompletesWithoutEncoding()
    {
        await OutputAsync();
        Assert.Equal(PreparationJobState.Succeeded, (await Executor(ffmpeg: "does-not-exist").ExecuteAsync(job.Id, Token)).FinalState);
    }

    [Fact]
    public async Task DiskReserveBlocksWithoutBurningRetries()
    {
        var result = await Executor(long.MaxValue).ExecuteAsync(job.Id, Token);
        Assert.Equal(PreparationJobState.Blocked, result.FinalState);
        Assert.Equal("insufficient-disk", result.ErrorCode);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task MissingOutputRequeuesSucceededJob()
    {
        var output = await OutputAsync();
        await Executor().ExecuteAsync(job.Id, Token);
        File.Delete(output);
        await PreparationScheduler.ScheduleCompatibilityJobsAsync(db, library.Id, root, NullLogger.Instance, Token);
        Assert.Equal(PreparationJobState.Queued, job.State);
        Assert.Equal(RenditionStatus.Stale, (await db.Renditions.SingleAsync(Token)).Status);
    }

    [Fact]
    public async Task ConcurrentFirstCreationReturnsOneJob()
    {
        db.PreparationJobs.Remove(job); await db.SaveChangesAsync(Token);
        async Task<long> Create()
        {
            await using var context = new AppDbContext(DbOptions);
            var result = await PreparationJobStore.GetOrCreateAsync(context, new PreparationJobEntity {
                LessonId = lesson.Id, SourceGeneration = 1, DedupKey = "concurrent", RecipeVersion = "test" }, Token);
            return result.Id;
        }
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Create, Token)));
        Assert.Single(ids.Distinct());
        Assert.Single(await db.PreparationJobs.AsNoTracking().ToListAsync(Token));
    }

    [Theory]
    [InlineData("/mnt/videos/course", "/mnt/videos")]
    [InlineData("/Volumes/External/course", "/Volumes/External")]
    [InlineData("/mnt/videos-other/course", "/")]
    public void ReserveSelectsContainingMount(string directory, string expected) =>
        Assert.Equal(expected, PreparationArtifacts.FindContainingMount(directory, ["/", "/mnt/videos", "/Volumes/External"]));

    [Theory]
    [InlineData("lesson.mp4", "h264", "aac")]
    [InlineData("lesson.mkv", "h264", "aac")]
    [InlineData("lesson.webm", "vp9", "opus")]
    [InlineData("lesson.m4v", "h264", "aac")]
    public void ExplicitRecipesSupportBrowserFallback(string name, string video, string audio) =>
        Assert.NotNull(PreparationRecipes.Choose(new SourceSetInfo(name, name, null, null,
            new ProbeMetadata(1000, video, audio, 640, 480)), out _));

    [Fact]
    public async Task RegistrationUsesConfiguredReserveAndStartsLockFirst()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddOptions();
        services.AddSingleton(Options.Create(App)); services.AddSingleton<SchemaReadiness>();
        services.AddCatalog(App, new PreparationOptions { DiskReserveBytes = 123456789 });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(123456789, provider.GetRequiredService<IOptions<PreparationOptions>>().Value.DiskReserveBytes);
        Assert.IsType<InstallationLockHolder>(provider.GetServices<IHostedService>().First());
        var first = new InstallationLockHolder(Options.Create(App));
        var second = new InstallationLockHolder(Options.Create(App));
        await first.StartAsync(Token);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(Token)); }
        finally { await first.StopAsync(Token); }
        await second.StartAsync(Token); await second.StopAsync(Token);
    }

    [Fact]
    public async Task ProgressIsPersistedBeforeEncodingFinishes()
    {
        if (OperatingSystem.IsWindows()) return;
        var wrapper = Path.Combine(root, "paced-ffmpeg");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nexec ffmpeg -re \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var running = Executor(ffmpeg: wrapper).ExecuteAsync(job.Id, Token);
        try
        {
            await using var observer = new AppDbContext(DbOptions);
            var observed = false;
            while (!running.IsCompleted)
            {
                var progress = await observer.PreparationJobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.Progress).SingleAsync(Token);
                if (progress is > 0 and < 1) { observed = true; break; }
                await Task.Delay(200, Token);
            }
            Assert.True(observed, "Progress should reach a different context before FFmpeg exits.");
        }
        finally { await running; }
        Assert.Equal(PreparationJobState.Succeeded, job.State);
    }

    [Theory]
    [InlineData("extra.m4v")]
    [InlineData("extra.webm")]
    public async Task NonExemptContainersAreScheduled(string filename)
    {
        var extra = new LessonEntity { CourseId = lesson.CourseId, PrimaryRelativePath = "Course/" + filename };
        extra.SourceComponents.Add(new SourceComponentEntity { Generation = 1, Role = SourceComponentRole.Video,
            RelativePath = extra.PrimaryRelativePath, ProbeMetadata = "{\"VideoCodec\":\"h264\",\"AudioCodec\":\"aac\"}" });
        db.Lessons.Add(extra); await db.SaveChangesAsync(Token);
        await PreparationScheduler.ScheduleCompatibilityJobsAsync(db, library.Id, root, NullLogger.Instance, Token);
        Assert.Equal(PreparationJobState.Queued, (await db.PreparationJobs.SingleAsync(j => j.LessonId == extra.Id, Token)).State);
    }

    [Fact]
    public async Task FilenameAloneNeverHidesAnOriginalLesson()
    {
        File.Copy(Source, Path.Combine(root, "Course/intro.tvp-demo.wmv"));
        var run = new ScanRunEntity { State = "Running" }; db.ScanRuns.Add(run); await db.SaveChangesAsync(Token);
        var scanner = new LibraryScanner(db, new FileSystemLibraryEnumerator(), new FFprobeAdapter("ffprobe"), NullLogger<LibraryScanner>.Instance);
        var outcome = await scanner.ExecuteAsync(run.Id, library, root, Token);
        Assert.True(outcome.Succeeded);
        Assert.Contains(await db.Lessons.ToListAsync(Token), l => l.PrimaryRelativePath == "Course/intro.tvp-demo.wmv");
    }

    [Fact]
    public async Task RecoveryPreservesUnverifiedArtifacts()
    {
        var output = await OutputAsync(corruptHash: true);
        var before = File.ReadAllBytes(output + ".tvp.json");
        await Executor().RecoverInterruptedAsync(Token);
        Assert.Equal(PreparationJobState.Failed, job.State);
        Assert.Empty(await db.Renditions.ToListAsync(Token));
        Assert.Equal(before, File.ReadAllBytes(output + ".tvp.json"));
    }

    [Fact]
    public async Task CrashBeforeRenameCleansOnlyRecordedTemporary()
    {
        var temporary = Path.Combine(root, $"Course/.video.tvp-0123456789ab.mp4.{job.Id}.part");
        await File.WriteAllTextAsync(temporary, "partial", Token);
        var unrelated = temporary + ".unrelated";
        await File.WriteAllTextAsync(unrelated, "keep", Token);
        db.PreparationAttempts.Add(new PreparationAttemptEntity { JobId = job.Id, TempRelativePath = Path.GetRelativePath(root, temporary) });
        await db.SaveChangesAsync(Token);
        await Executor().RecoverInterruptedAsync(Token);
        Assert.False(File.Exists(temporary));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(PreparationJobState.Queued, job.State);
    }

    [Fact]
    public async Task TransientFailuresRespectAttemptBound()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            job.Attempt = attempt; job.State = PreparationJobState.Running; await db.SaveChangesAsync(Token);
            var outcome = await Executor(ffmpeg: "nonexistent-tuts-test-ffmpeg").ExecuteAsync(job.Id, Token);
            Assert.Equal(attempt < 3 ? PreparationJobState.Queued : PreparationJobState.Failed, outcome.FinalState);
        }
    }

    [Fact]
    public async Task StaleQueuePauseReturns412AndCurrentRevision()
    {
        var controller = new PreparationsController(db) { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        controller.Request.Headers.IfMatch = "\"999\"";
        var response = await controller.SetQueuePaused(new TutsVideoPlayer.Web.Models.QueuePauseModel(true), Token);
        Assert.Equal(412, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(response).StatusCode);
        Assert.False(string.IsNullOrEmpty(controller.Response.Headers.ETag));
    }

    [Fact]
    public async Task LinkedLibraryRootPublishesContainedRelativePath()
    {
        if (OperatingSystem.IsWindows()) return;
        var alias = root + "-alias";
        Directory.CreateSymbolicLink(alias, root);
        try
        {
            var executor = new PreparationExecutor(db, Options.Create(new AppOptions { LibraryRoot = alias, DataDirectory = root }),
                Options.Create(new PreparationOptions { DiskReserveBytes = 0 }), new FFprobeAdapter("ffprobe"), new FFmpegAdapter("ffmpeg"),
                new PreparedOutputValidator(new FFprobeAdapter("ffprobe")), NullLogger<PreparationExecutor>.Instance);
            var outcome = await executor.ExecuteAsync(job.Id, Token);
            Assert.Equal(PreparationJobState.Succeeded, outcome.FinalState);
            var rendition = await db.Renditions.SingleAsync(Token);
            Assert.StartsWith("Course/", rendition.RelativePath, StringComparison.Ordinal);
            Assert.True(LibraryPathGuard.TryResolveWithin(alias, rendition.RelativePath, out _));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task ShutdownKeepsTemporaryOwnershipForRecovery()
    {
        if (OperatingSystem.IsWindows()) return;
        var wrapper = Path.Combine(root, "slow-ffmpeg");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nexec ffmpeg -re \"$@\"\n", Token);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var executing = Executor(ffmpeg: wrapper).ExecuteAsync(job.Id, cancellation.Token);
        await using var observer = new AppDbContext(DbOptions);
        string? temporary = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !executing.IsCompleted)
        {
            temporary = await observer.PreparationAttempts.AsNoTracking().Where(a => a.JobId == job.Id)
                .Select(a => a.TempRelativePath).FirstOrDefaultAsync(Token);
            if (temporary is not null && File.Exists(Path.Combine(root, temporary))) break;
            await Task.Delay(50, Token);
        }
        cancellation.Cancel();
        var outcome = await executing;
        Assert.Equal(PreparationJobState.Interrupted, outcome.FinalState);
        var recorded = (await db.PreparationAttempts.SingleAsync(Token)).TempRelativePath;
        Assert.NotNull(recorded);
        Assert.False(Path.IsPathRooted(recorded));
        await Executor().RecoverInterruptedAsync(Token);
        Assert.False(File.Exists(Path.Combine(root, recorded)));
        Assert.Equal(PreparationJobState.Queued, job.State);
    }

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
