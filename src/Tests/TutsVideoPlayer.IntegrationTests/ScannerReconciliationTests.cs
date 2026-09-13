using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Regression coverage for the reconciliation defects found in the M1 library review:
/// probe-cache generation filtering, failure persistence, nested folder creation, probe
/// retry, companion presence changes, and content-based source identity.
/// </summary>
public class ScannerReconciliationTests : IAsyncLifetime
{
    private const string GettingStarted = "CourseAlpha/01 Intro/02 Getting Started.mp4";

    private ScannerHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = new ScannerHarness();
        await _harness.InitializeAsync();
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private string Absolute(string relativePath) =>
        Path.Combine(_harness.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<LessonEntity> LessonAsync(string relativePath, CancellationToken token) =>
        await _harness.Context.Lessons
            .Include(lesson => lesson.SourceComponents)
            .SingleAsync(lesson => lesson.PrimaryRelativePath == relativePath, token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFingerprintPreservesVerifiedSourceAndRetriesAfterRecovery(bool companion)
    {
        if (OperatingSystem.IsWindows()) return;

        var token = TestContext.Current.CancellationToken;
        const string lessonPath = "CourseTS/lessonA.ts";
        var sourcePath = companion ? "CourseTS/lessonA_audio.aac" : lessonPath;
        var absolute = Absolute(sourcePath);
        var backup = absolute + ".temporarily-unreadable";
        var stub = Path.Combine(_harness.Root, "probe.sh");
        const string output = "printf '%s\\n' '{\"format\":{\"duration\":\"12.500\"},\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":640,\"height\":360}]}'\n";
        await File.WriteAllTextAsync(stub, "#!/bin/sh\n" + output, token);
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _harness.UseProbe(stub);
        await _harness.RunScanAsync(token);

        var before = await LessonAsync(lessonPath, token);
        var original = before.SourceComponents.Single(component => component.RelativePath == sourcePath);
        var generation = before.SourceGeneration;
        var digest = original.Sha256;
        var length = original.LengthBytes;
        var modified = original.ModifiedUtcMs;
        Assert.NotNull(digest);

        await File.WriteAllBytesAsync(absolute, [9, 8, 7, 6, 5, 4, 3], token);
        // Force the video probe to run even when only its companion changed. Move the
        // candidate after enumeration but before hashing, simulating an I/O failure.
        File.SetLastWriteTimeUtc(Absolute(lessonPath), DateTime.UtcNow.AddMinutes(1));
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        await File.WriteAllTextAsync(stub,
            $"#!/bin/sh\nif [ -f {Quote(absolute)} ]; then mv {Quote(absolute)} {Quote(backup)}; fi\n" + output, token);
        try
        {
            var failedInspection = await _harness.RunScanAsync(token);
            Assert.Equal("Succeeded", failedInspection.State);
            _harness.Context.ChangeTracker.Clear();
            var unresolved = await LessonAsync(lessonPath, token);
            var retained = unresolved.SourceComponents.Single(component => component.RelativePath == sourcePath);
            Assert.Equal(generation, unresolved.SourceGeneration);
            Assert.Equal(digest, retained.Sha256);
            Assert.Equal(length, retained.LengthBytes);
            Assert.Equal(modified, retained.ModifiedUtcMs);
            Assert.True(await _harness.Context.ScanIssues.AnyAsync(issue =>
                issue.ScanRunId == failedInspection.Id && issue.Code == "FingerprintFailed", token));
        }
        finally
        {
            if (File.Exists(backup)) File.Move(backup, absolute);
            await File.WriteAllTextAsync(stub, "#!/bin/sh\n" + output, token);
        }

        Assert.Equal("Succeeded", (await _harness.RunScanAsync(token)).State);
        _harness.Context.ChangeTracker.Clear();
        var recovered = await LessonAsync(lessonPath, token);
        Assert.Equal(generation + 1, recovered.SourceGeneration);
        var current = recovered.SourceComponents.Single(component =>
            component.RelativePath == sourcePath && component.Generation == recovered.SourceGeneration);
        Assert.NotEqual(digest, current.Sha256);
        Assert.Equal(7, current.LengthBytes);
        Assert.Equal("Succeeded", (await _harness.RunScanAsync(token)).State);
        Assert.Equal(generation + 1, recovered.SourceGeneration);
    }

    [Fact]
    public async Task ChangedBytesOfEqualLengthStartANewSourceGeneration()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        var before = await LessonAsync(GettingStarted, token);
        var originalGeneration = before.SourceGeneration;
        Assert.Equal(1, originalGeneration);
        var originalDigest = before.SourceComponents
            .Single(component => component.Role == SourceComponentRole.Video).Sha256;
        Assert.NotNull(originalDigest);

        // Identical length, and nothing ffprobe reports would differ either: only the bytes
        // themselves say this is a different video.
        await File.WriteAllBytesAsync(Absolute(GettingStarted), [9, 9, 9, 9], token);

        var second = await _harness.RunScanAsync(token);
        Assert.Equal("Succeeded", second.State);

        _harness.Context.ChangeTracker.Clear();
        var after = await LessonAsync(GettingStarted, token);
        Assert.Equal(originalGeneration + 1, after.SourceGeneration);
        var currentVideo = after.SourceComponents
            .Single(component => component.Generation == after.SourceGeneration
                && component.Role == SourceComponentRole.Video);
        Assert.NotEqual(originalDigest, currentVideo.Sha256);
    }

    [Fact]
    public async Task ScansAfterASourceGenerationChangeKeepSucceeding()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);
        await File.WriteAllBytesAsync(Absolute(GettingStarted), [9, 9, 9, 9], token);

        // Two generations now share one relative path. A probe cache that includes both
        // throws a duplicate-key ArgumentException on every later scan.
        var second = await _harness.RunScanAsync(token);
        var third = await _harness.RunScanAsync(token);

        Assert.Equal("Succeeded", second.State);
        Assert.Equal("Succeeded", third.State);
        Assert.False(await _harness.Context.ScanIssues.AnyAsync(issue => issue.Code == "ScanAborted", token));
    }

    [Fact]
    public async Task NestedLessonFoldersCreateEveryIntermediateAncestor()
    {
        var token = TestContext.Current.CancellationToken;
        var nested = Path.Combine(_harness.Root, "CourseNested", "Parent", "Child");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "video.mp4"), [1, 2, 3], token);

        var run = await _harness.RunScanAsync(token);
        Assert.Equal("Succeeded", run.State);

        var folders = await _harness.Context.LessonFolders
            .Where(folder => folder.Course.RelativeDirectory == "CourseNested")
            .ToListAsync(token);

        var parent = folders.Single(folder => folder.RelativeDirectory == "CourseNested/Parent");
        var child = folders.Single(folder => folder.RelativeDirectory == "CourseNested/Parent/Child");
        Assert.Null(parent.ParentFolderId);
        Assert.Equal(parent.Id, child.ParentFolderId);

        var lesson = await LessonAsync("CourseNested/Parent/Child/video.mp4", token);
        Assert.Equal(child.Id, lesson.FolderId);
        Assert.Equal(CatalogAvailability.Available, lesson.Availability);
    }

    [Fact]
    public async Task AFailedReconciliationIsPersistedAndLaterScansStillRun()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        // A duplicate subtitle path makes reconciliation throw after the transaction opened.
        _harness.Enumerator.ExtraFiles.Add(new LibraryFileEntry("CourseSubs/video.srt", 42, 1));
        ScanRunEntity failedRun;
        try
        {
            failedRun = await _harness.RunScanAsync(token);
        }
        finally
        {
            _harness.Enumerator.ExtraFiles.Clear();
        }

        _harness.Context.ChangeTracker.Clear();
        var persisted = await _harness.Context.ScanRuns.SingleAsync(run => run.Id == failedRun.Id, token);
        Assert.Equal("Failed", persisted.State);
        Assert.NotNull(persisted.FinishedUtcMs);
        Assert.True(await _harness.Context.ScanIssues.AnyAsync(
            issue => issue.ScanRunId == failedRun.Id && issue.Code == "ScanAborted",
            token));

        // A dead Running row would be joined by every later refresh instead of scanning.
        var recovered = await _harness.RunScanAsync(token);
        Assert.Equal("Succeeded", recovered.State);
    }

    [Fact]
    public async Task AddingAndRemovingCompanionAudioIsReconciled()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        var lessonB = await LessonAsync("CourseTS/lessonB.ts", token);
        Assert.Single(lessonB.SourceComponents);
        var firstGeneration = lessonB.SourceGeneration;

        var companion = Absolute("CourseTS/lessonB_audio.aac");
        await File.WriteAllBytesAsync(companion, [7, 7], token);
        await _harness.RunScanAsync(token);

        _harness.Context.ChangeTracker.Clear();
        var paired = await LessonAsync("CourseTS/lessonB.ts", token);
        // Captured now: the scanner shares this context, so a later scan would mutate the
        // tracked instance behind this reference.
        var pairedGeneration = paired.SourceGeneration;
        Assert.True(pairedGeneration > firstGeneration);
        var current = paired.SourceComponents
            .Where(component => component.Generation == pairedGeneration)
            .ToList();
        Assert.Equal(2, current.Count);
        Assert.Contains(current, component =>
            component.Role == SourceComponentRole.CompanionAudio
            && component.RelativePath == "CourseTS/lessonB_audio.aac");

        File.Delete(companion);
        var removalRun = await _harness.RunScanAsync(token);
        Assert.Equal("Succeeded", removalRun.State);

        _harness.Context.ChangeTracker.Clear();
        var unpaired = await LessonAsync("CourseTS/lessonB.ts", token);
        Assert.True(unpaired.SourceGeneration > pairedGeneration);
        var latest = unpaired.SourceComponents
            .Where(component => component.Generation == unpaired.SourceGeneration)
            .ToList();
        Assert.Equal(SourceComponentRole.Video, Assert.Single(latest).Role);
    }

    [Fact]
    public async Task RepairingTheProbeRecoversMetadataOnTheNextScan()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var token = TestContext.Current.CancellationToken;

        // The first scan probes with the real ffprobe, which cannot read a 4-byte fixture.
        await _harness.RunScanAsync(token);
        var beforeRepair = await LessonAsync(GettingStarted, token);
        var generationBeforeRepair = beforeRepair.SourceGeneration;
        Assert.Null(beforeRepair.DurationMs);
        Assert.Null(beforeRepair.SourceComponents
            .Single(component => component.Role == SourceComponentRole.Video).ProbeMetadata);

        var stub = Path.Combine(_harness.Root, "..", $"tuts-probe-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(
            stub,
            "#!/bin/sh\ncat <<'JSON'\n{\"format\":{\"duration\":\"12.500\"},\"streams\":[" +
            "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":640,\"height\":360}," +
            "{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}]}\nJSON\n",
            token);
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            _harness.UseProbe(Path.GetFullPath(stub));

            // Nothing about the files changed; only the dependency was repaired. A cache hit
            // based on size and modification time alone would leave the duration unknown.
            var run = await _harness.RunScanAsync(token);
            Assert.Equal("Succeeded", run.State);

            _harness.Context.ChangeTracker.Clear();
            var afterRepair = await LessonAsync(GettingStarted, token);
            Assert.Equal(12500, afterRepair.DurationMs);
            Assert.Equal(generationBeforeRepair, afterRepair.SourceGeneration);
            Assert.Contains("h264", afterRepair.SourceComponents
                .Single(component => component.Generation == afterRepair.SourceGeneration
                    && component.Role == SourceComponentRole.Video).ProbeMetadata!);
        }
        finally
        {
            File.Delete(stub);
        }
    }
}
