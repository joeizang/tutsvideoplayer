using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;
using TutsVideoPlayer.Web.Features.Preparation;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Regressions for the M5 review: eviction must never take a rendition that is being
/// watched, must refuse anything outside the managed quality area, must charge and reclaim
/// stale copies, and crash recovery must publish a quality output as a quality rendition.
/// </summary>
public sealed class QualityCacheReviewTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;
    private string _root = null!;
    private string _outside = null!;
    private long _lessonId;

    public async ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("tuts-cache-").FullName;
        _outside = Directory.CreateTempSubdirectory("tuts-outside-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "CoursePrep"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "CoursePrep", "lesson.mp4"), new byte[512]);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _context.Database.MigrateAsync();

        var library = new LibraryEntity { DisplayName = "L", LogicalIdentity = Guid.NewGuid().ToString() };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        var course = new CourseEntity
        {
            LibraryId = library.Id, RelativeDirectory = "CoursePrep", DisplayTitle = "CoursePrep",
            SearchTitle = "courseprep", SortKey = "CoursePrep", Availability = CatalogAvailability.Available
        };
        _context.Courses.Add(course);
        await _context.SaveChangesAsync();
        var lesson = new LessonEntity
        {
            CourseId = course.Id, PrimaryRelativePath = "CoursePrep/lesson.mp4", Title = "lesson",
            // Matches the one-second clip the recovery test encodes, so the validator's
            // duration check compares like with like.
            SearchTitle = "lesson", SortKey = "lesson", DurationMs = 1_000,
            Availability = CatalogAvailability.Available
        };
        _context.Lessons.Add(lesson);
        await _context.SaveChangesAsync();
        _lessonId = lesson.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private IOptions<AppOptions> AppOptions() =>
        Options.Create(new AppOptions { LibraryRoot = _root, DataDirectory = _root });

    private CacheEvictionService CreateEviction() =>
        new(_context, AppOptions(), NullLogger<CacheEvictionService>.Instance);

    private CacheAccounting CreateAccounting() => new(_context, AppOptions());

    /// <summary>Writes a managed quality artifact and returns its library-relative path.</summary>
    private async Task<(RenditionEntity Rendition, string FullPath)> AddQualityRenditionAsync(
        string profile,
        long bytes,
        RenditionStatus status = RenditionStatus.Ready,
        long? lastAccessUtcMs = null)
    {
        var directory = Path.Combine(_root, ".tutsvideoplayer", "quality",
            _lessonId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1");
        Directory.CreateDirectory(directory);
        var fileName = $"lesson-{profile}-{Guid.NewGuid():N}"[..24] + ".mp4";
        var fullPath = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(fullPath, new byte[bytes]);

        var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        var rendition = new RenditionEntity
        {
            LessonId = _lessonId,
            SourceGeneration = 1,
            Purpose = RenditionPurpose.Quality,
            RetentionClass = RenditionRetention.Quality,
            Profile = profile,
            Status = status,
            RelativePath = relative,
            ManifestPath = relative + ".tvp.json",
            ByteLength = bytes,
            LastAccessUtcMs = lastAccessUtcMs
        };
        _context.Renditions.Add(rendition);
        await _context.SaveChangesAsync();
        return (rendition, fullPath);
    }

    private async Task LeaseAsync(long renditionId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _context.LessonProgress.Add(new LessonProgressEntity { LessonId = _lessonId, SourceGeneration = 1 });
        var session = new PlaybackSessionEntity
        {
            LessonId = _lessonId,
            SourceGeneration = 1,
            StartedUtcMs = now,
            LastHeartbeatUtcMs = now,
            ActiveRenditionId = renditionId
        };
        _context.PlaybackSessions.Add(session);
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task ARenditionWithALiveLeaseIsNeverEvicted()
    {
        var token = TestContext.Current.CancellationToken;
        var (watched, watchedPath) = await AddQualityRenditionAsync("720", 4_000, lastAccessUtcMs: 1);
        await LeaseAsync(watched.Id);

        // The limit is far below usage and the watched copy is the only candidate.
        var outcome = await CreateEviction().EvictUntilUnderLimitAsync(1_000, token);

        Assert.Equal(0, outcome.EvictedCount);
        Assert.True(File.Exists(watchedPath), "The file backing a live playback session was deleted.");
        // The shortfall is reported so the caller can defer instead of interrupting playback.
        Assert.Equal(3_000, outcome.PendingBytes);

        _context.ChangeTracker.Clear();
        var stored = await _context.Renditions.SingleAsync(r => r.Id == watched.Id, token);
        Assert.Equal(RenditionStatus.Ready, stored.Status);
    }

    [Fact]
    public async Task AColdCopyIsEvictedWhileTheWatchedOneSurvives()
    {
        var token = TestContext.Current.CancellationToken;
        var (watched, watchedPath) = await AddQualityRenditionAsync("720", 4_000, lastAccessUtcMs: 1);
        var (cold, coldPath) = await AddQualityRenditionAsync("480", 4_000, lastAccessUtcMs: 2);
        await LeaseAsync(watched.Id);

        var outcome = await CreateEviction().EvictUntilUnderLimitAsync(4_000, token);

        Assert.Equal(1, outcome.EvictedCount);
        Assert.False(File.Exists(coldPath));
        Assert.True(File.Exists(watchedPath));

        _context.ChangeTracker.Clear();
        Assert.Equal(RenditionStatus.Evicted, (await _context.Renditions.SingleAsync(r => r.Id == cold.Id, token)).Status);
    }

    [Fact]
    public async Task EvictionRefusesAPathOutsideTheManagedQualityArea()
    {
        var token = TestContext.Current.CancellationToken;

        // A catalog row that points at someone else's file, reachable through a directory
        // symlink inside the library. Nothing outside the managed area may be deleted.
        var victim = Path.Combine(_outside, "not-ours.mp4");
        await File.WriteAllBytesAsync(victim, new byte[4_000], token);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), _outside);
        }

        var rendition = new RenditionEntity
        {
            LessonId = _lessonId,
            SourceGeneration = 1,
            Purpose = RenditionPurpose.Quality,
            RetentionClass = RenditionRetention.Quality,
            Profile = "720",
            Status = RenditionStatus.Ready,
            RelativePath = OperatingSystem.IsWindows() ? "linked/not-ours.mp4" : "linked/not-ours.mp4",
            ByteLength = 4_000,
            LastAccessUtcMs = 1
        };
        _context.Renditions.Add(rendition);
        await _context.SaveChangesAsync(token);

        await CreateEviction().EvictUntilUnderLimitAsync(0, token);

        Assert.True(File.Exists(victim), "A file outside the managed quality area was deleted.");
    }

    [Fact]
    public async Task StaleCopiesAreChargedAndReclaimedFirst()
    {
        var token = TestContext.Current.CancellationToken;
        var (stale, stalePath) = await AddQualityRenditionAsync("1080", 5_000, RenditionStatus.Stale, lastAccessUtcMs: 9_999);
        var (ready, readyPath) = await AddQualityRenditionAsync("720", 3_000, lastAccessUtcMs: 1);

        // Superseded bytes still occupy the disk, so they belong in the budget.
        Assert.Equal(8_000, await CreateAccounting().GetChargeableQualityBytesAsync(token));

        var outcome = await CreateEviction().EvictUntilUnderLimitAsync(3_000, token);

        // The stale copy goes first even though it was touched most recently: it cannot be
        // played at all, so reclaiming it costs nothing.
        Assert.Equal(1, outcome.EvictedCount);
        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(readyPath));
        Assert.Equal(3_000, await CreateAccounting().GetChargeableQualityBytesAsync(token));
        _context.ChangeTracker.Clear();
        Assert.Equal(RenditionStatus.Evicted, (await _context.Renditions.SingleAsync(r => r.Id == stale.Id, token)).Status);
        Assert.Equal(RenditionStatus.Ready, (await _context.Renditions.SingleAsync(r => r.Id == ready.Id, token)).Status);
    }

    [Fact]
    public async Task AnInFlightQualityJobIsChargedForItsGrowingTemporaryFile()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(_root, ".tutsvideoplayer", "quality",
            _lessonId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".lesson-720-abc.mp4.1.part");
        await File.WriteAllBytesAsync(temporary, new byte[7_000], token);

        var job = new PreparationJobEntity
        {
            LessonId = _lessonId, SourceGeneration = 1, DedupKey = Guid.NewGuid().ToString(),
            Purpose = RenditionPurpose.Quality, Profile = "720", RecipeVersion = "quality-720-v1",
            State = PreparationJobState.Running, EnqueuedUtcMs = 1, ReservedBytes = 500,
            // The final output does not exist yet; measuring it always reported zero.
            OutputRelativePath = ".tutsvideoplayer/quality/1/1/lesson-720-abc.mp4"
        };
        _context.PreparationJobs.Add(job);
        await _context.SaveChangesAsync(token);
        _context.PreparationAttempts.Add(new PreparationAttemptEntity
        {
            JobId = job.Id,
            Attempt = 1,
            StartedUtcMs = 1,
            TempRelativePath = Path.GetRelativePath(_root, temporary).Replace('\\', '/')
        });
        await _context.SaveChangesAsync(token);

        var accounting = CreateAccounting();
        Assert.Equal(7_000, await accounting.GetReservedQualityBytesAsync(token));
        // A job can ask what everything else has claimed without counting itself twice.
        Assert.Equal(0, await accounting.GetReservedQualityBytesAsync(job.Id, token));
    }

    [Fact]
    public async Task ACrashedQualityJobRecoversAsAQualityRendition()
    {
        var token = TestContext.Current.CancellationToken;

        var directory = Path.Combine(_root, ".tutsvideoplayer", "quality",
            _lessonId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1");
        Directory.CreateDirectory(directory);

        // A real encode, so the output validates: a tiny h264 clip scaled like a 480p copy.
        var outputPath = Path.Combine(directory, "lesson-480-deadbeefcafe.mp4");
        var ffmpeg = new FFmpegAdapter("ffmpeg");
        var encode = await ffmpeg.RunAsync(
        [
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
            "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100",
            "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac",
            "-movflags", "+faststart", "-f", "mp4", "-y", outputPath
        ], null, token);
        Assert.True(encode.Success, encode.Error);

        var sourceFingerprint = SourceFingerprint.Compute(
            [("video", "CoursePrep/lesson.mp4", Path.Combine(_root, "CoursePrep", "lesson.mp4"))]);
        var probeAdapter = new FFprobeAdapter("ffprobe");
        var probed = await probeAdapter.ProbeAsync(outputPath, token);
        Assert.True(probed.Success, probed.Error);

        var manifestPath = outputPath + ".tvp.json";
        new PreparationManifest
        {
            JobId = 1,
            LessonId = _lessonId,
            SourceGeneration = 1,
            RecipeVersion = "quality-480-v1",
            Sources = [new ManifestSource("video", "CoursePrep/lesson.mp4", sourceFingerprint)],
            OutputFileName = Path.GetFileName(outputPath),
            OutputHash = PreparationArtifacts.Hash(outputPath),
            OutputLengthBytes = new FileInfo(outputPath).Length,
            DurationMs = probed.DurationMs,
            Width = probed.Width,
            Height = probed.Height,
            VideoCodec = probed.VideoCodec,
            AudioCodec = probed.AudioCodec,
            State = "Prepared"
        }.WriteAtomically(manifestPath);

        _context.SourceComponents.Add(new SourceComponentEntity
        {
            LessonId = _lessonId, Generation = 1, Role = SourceComponentRole.Video,
            RelativePath = "CoursePrep/lesson.mp4", LengthBytes = 512, ModifiedUtcMs = 1
        });
        await _context.SaveChangesAsync(token);

        // The crash window: the file is renamed into place and the manifest written, but the
        // process died before the database row was published.
        var job = new PreparationJobEntity
        {
            LessonId = _lessonId, SourceGeneration = 1, DedupKey = Guid.NewGuid().ToString(),
            Purpose = RenditionPurpose.Quality, Profile = "480", RecipeVersion = "quality-480-v1",
            State = PreparationJobState.Publishing, EnqueuedUtcMs = 1, Attempt = 1,
            OutputRelativePath = Path.GetRelativePath(_root, outputPath).Replace('\\', '/'),
            ManifestRelativePath = Path.GetRelativePath(_root, manifestPath).Replace('\\', '/')
        };
        _context.PreparationJobs.Add(job);
        await _context.SaveChangesAsync(token);

        var executor = new PreparationExecutor(
            _context, AppOptions(), Options.Create(new PreparationOptions()),
            probeAdapter, ffmpeg, new PreparedOutputValidator(probeAdapter),
            CreateAccounting(), CreateEviction(), NullLogger<PreparationExecutor>.Instance);

        await executor.RecoverInterruptedAsync(token);

        _context.ChangeTracker.Clear();
        var recoveredJob = await _context.PreparationJobs.SingleAsync(candidate => candidate.Id == job.Id, token);
        Assert.Equal(PreparationJobState.Succeeded, recoveredJob.State);

        var rendition = await _context.Renditions.SingleAsync(
            candidate => candidate.LessonId == _lessonId && candidate.SourceGeneration == 1, token);

        // Publishing every recovered job as a compatibility copy registered quality outputs
        // as Permanent with no profile, which escapes the cache budget entirely.
        Assert.Equal(RenditionPurpose.Quality, rendition.Purpose);
        Assert.Equal(RenditionRetention.Quality, rendition.RetentionClass);
        Assert.Equal("480", rendition.Profile);
        Assert.Equal(RenditionStatus.Ready, rendition.Status);
        Assert.True(await CreateAccounting().GetChargeableQualityBytesAsync(token) > 0);
    }
}
