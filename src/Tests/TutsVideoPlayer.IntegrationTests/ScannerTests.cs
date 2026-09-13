using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.IntegrationTests;

public class ScannerTests : IAsyncLifetime
{
    private ScannerHarness _harness = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = new ScannerHarness();
        await _harness.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedScansProduceStableIdsAndNaturalOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var firstRun = await _harness.RunScanAsync(token);
        var firstLessons = await _harness.Context.Lessons
            .Where(lesson => lesson.Course.RelativeDirectory == "CourseAlpha")
            .OrderBy(lesson => lesson.SortKey)
            .Select(lesson => new { lesson.Id, lesson.PrimaryRelativePath })
            .ToListAsync(token);

        var secondRun = await _harness.RunScanAsync(token);
        var secondLessons = await _harness.Context.Lessons
            .Where(lesson => lesson.Course.RelativeDirectory == "CourseAlpha")
            .OrderBy(lesson => lesson.SortKey)
            .Select(lesson => new { lesson.Id, lesson.PrimaryRelativePath })
            .ToListAsync(token);

        Assert.Equal(3, firstLessons.Count);
        Assert.Equal(firstLessons, secondLessons);
        Assert.Equal("Succeeded", firstRun.State);
        Assert.Equal("Succeeded", secondRun.State);

        Assert.Equal("CourseAlpha/01 Intro/02 Getting Started.mp4", firstLessons[0].PrimaryRelativePath);
        Assert.Equal("CourseAlpha/03 Setup/02 Basics.mp4", firstLessons[1].PrimaryRelativePath);
        Assert.Equal("CourseAlpha/03 Setup/10 Advanced.mp4", firstLessons[2].PrimaryRelativePath);
    }

    [Fact]
    public async Task TsAacPairsFormOneLessonEach()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        var tsLessons = await _harness.Context.Lessons
            .Where(lesson => lesson.Course.RelativeDirectory == "CourseTS")
            .OrderBy(lesson => lesson.SortKey)
            .ToListAsync(token);

        Assert.Equal(3, tsLessons.Count);

        var paired = tsLessons.Single(lesson => lesson.PrimaryRelativePath.EndsWith("lessonA.ts", StringComparison.Ordinal));
        Assert.Equal(2, paired.SourceComponents.Count);

        var pairedC = tsLessons.Single(lesson => lesson.PrimaryRelativePath.EndsWith("lessonC.ts", StringComparison.Ordinal));
        Assert.Equal(2, pairedC.SourceComponents.Count);

        var audioMissing = tsLessons.Single(lesson => lesson.PrimaryRelativePath.EndsWith("lessonB.ts", StringComparison.Ordinal));
        Assert.Single(audioMissing.SourceComponents);

        var issues = await _harness.Context.ScanIssues
            .Where(issue => issue.Code == "MissingCompanionAudio")
            .ToListAsync(token);
        Assert.Equal("CourseTS/lessonB.ts", issues.Single().RelativePath);
    }

    [Fact]
    public async Task DeletedLessonsBecomeMissingWhileOthersRemainAvailable()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);
        File.Delete(Path.Combine(_harness.Root, "CourseAlpha", "03 Setup", "10 Advanced.mp4"));

        await _harness.RunScanAsync(token);

        var advanced = (await _harness.Context.Lessons.ToListAsync(token)).Single(lesson =>
            lesson.PrimaryRelativePath.EndsWith("10 Advanced.mp4", StringComparison.Ordinal));
        Assert.Equal(CatalogAvailability.Missing, advanced.Availability);

        var basics = (await _harness.Context.Lessons.ToListAsync(token)).Single(lesson =>
            lesson.PrimaryRelativePath.EndsWith("02 Basics.mp4", StringComparison.Ordinal));
        Assert.Equal(CatalogAvailability.Available, basics.Availability);
    }

    [Fact]
    public async Task FailedSubtreeIsNotDeclaredMissing()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        _harness.Enumerator.FailingDirectories.Add("CourseAlpha/03 Setup");
        try
        {
            await _harness.RunScanAsync(token);

            var lessons = await _harness.Context.Lessons.ToListAsync(token);
            var advanced = lessons.Single(lesson => lesson.PrimaryRelativePath.EndsWith("10 Advanced.mp4", StringComparison.Ordinal));
            var basics = lessons.Single(lesson => lesson.PrimaryRelativePath.EndsWith("02 Basics.mp4", StringComparison.Ordinal));

            Assert.Equal(CatalogAvailability.Available, advanced.Availability);
            Assert.Equal(CatalogAvailability.Available, basics.Availability);

            var issue = await _harness.Context.ScanIssues.SingleAsync(
                issue => issue.Code == "DirectoryEnumerationFailed",
                token);
            Assert.Equal("CourseAlpha/03 Setup", issue.RelativePath);
        }
        finally
        {
            _harness.Enumerator.FailingDirectories.Clear();
        }
    }

    [Fact]
    public async Task UnavailableRootFailsTheScanWithoutTouchingTheCatalog()
    {
        var token = TestContext.Current.CancellationToken;
        var firstRun = await _harness.RunScanAsync(token);
        Assert.Equal("Succeeded", firstRun.State);
        var lessonsBefore = await _harness.Context.Lessons.CountAsync(token);

        var root = _harness.Root;
        var backup = Path.Combine(Path.GetTempPath(), $"tuts-hidden-{Guid.NewGuid():N}");
        Directory.Move(root, backup);
        try
        {
            var failedRun = await _harness.RunScanAsync(token);

            Assert.Equal("Failed", failedRun.State);
            Assert.Equal(lessonsBefore, await _harness.Context.Lessons.CountAsync(token));
            Assert.Equal(
                lessonsBefore,
                await _harness.Context.Lessons.CountAsync(lesson => lesson.Availability == CatalogAvailability.Available, token));
            Assert.True(await _harness.Context.ScanIssues.AnyAsync(issue => issue.Code == "RootUnavailable", token));
        }
        finally
        {
            Directory.Move(backup, root);
        }
    }

    [Fact]
    public async Task RootLevelVideosAreReportedAsUnsupportedOrganization()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        Assert.Equal(0, await _harness.Context.Lessons.CountAsync(lesson => lesson.PrimaryRelativePath == "loose.mp4", token));
        Assert.True(await _harness.Context.ScanIssues.AnyAsync(issue => issue.Code == "RootLevelVideo", token));
    }

    [Fact]
    public async Task EmptyDirectoriesDoNotBecomeCourses()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        Assert.False(await _harness.Context.Courses.AnyAsync(course => course.RelativeDirectory == "EmptyDirectory", token));
        Assert.Equal(4, await _harness.Context.Courses.CountAsync(token));
    }

    [Fact]
    public async Task ReservedDirectoriesAndSymlinksAreExcluded()
    {
        var token = TestContext.Current.CancellationToken;
        var outsideDir = Directory.CreateTempSubdirectory("tuts-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_harness.Root, "linked-dir-link"), outsideDir);
            await _harness.RunScanAsync(token);

            var lessons = await _harness.Context.Lessons.ToListAsync(token);
            Assert.DoesNotContain(lessons, lesson => lesson.PrimaryRelativePath.Contains(".tutsvideoplayer", StringComparison.Ordinal));
            Assert.DoesNotContain(lessons, lesson => lesson.PrimaryRelativePath.Contains("linked-dir-link", StringComparison.Ordinal));
            Assert.True(await _harness.Context.ScanIssues.AnyAsync(issue => issue.Code == "SymlinkExcluded", token));
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task SubtitleCandidatesAreDiscoveredPerCourse()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        var subtitles = await _harness.Context.SubtitleTracks
            .Where(track => track.Course.RelativeDirectory == "CourseSubs")
            .OrderBy(track => track.RelativePath)
            .ToListAsync(token);

        Assert.Equal(3, subtitles.Count);
        Assert.Contains(subtitles, track => track.Format == "Srt");
        Assert.Contains(subtitles, track => track.Format == "Vtt");
    }

    [Fact]
    public async Task ProbeFailuresAreVisibleIssuesAndLessonsKeepUnknownDuration()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        Assert.True(await _harness.Context.ScanIssues.AnyAsync(issue => issue.Code == "ProbeFailed", token));
        var alphaLesson = await _harness.Context.Lessons
            .Where(lesson => lesson.Course.RelativeDirectory == "CourseAlpha")
            .OrderBy(lesson => lesson.Id)
            .FirstAsync(token);
        Assert.Null(alphaLesson.DurationMs);
    }

    [Fact]
    public async Task OrphanCompanionAudioIsReported()
    {
        var token = TestContext.Current.CancellationToken;
        await _harness.RunScanAsync(token);

        Assert.True(await _harness.Context.ScanIssues.AnyAsync(issue => issue.Code == "OrphanCompanionAudio", token));
    }
}