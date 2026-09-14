using System.Net;
using System.Net.Http.Json;
using System.Text;
using TutsVideoPlayer.Web.Features.Subtitles;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Regressions for the M3 review comments: switching between two existing tracks, invalidating
/// normalized captions when the source changes, refusing undecodable bytes, keeping a manual
/// preference whose sidecar disappeared, and resolving automatic selection to a real track.
/// </summary>
[Collection(TestApplication.CollectionName)]
public class SubtitleReviewRegressionTests(TestApplication application)
{
    private HttpClient CreateGuardedClient()
    {
        var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.RequestHeaderName, "1");
        return client;
    }

    private async Task<HttpClient> GetReadyClientAsync()
    {
        var client = CreateGuardedClient();
        await application.EnsureCatalogScannedAsync();
        return client;
    }

    private async Task<CourseTreeNodeModel> GetLessonAsync(HttpClient client, string courseTitle, string fileSuffix)
    {
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var course = courses!.Courses.Single(candidate => candidate.Title == courseTitle);
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{course.Id}/tree", TestContext.Current.CancellationToken);
        return tree!.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith(fileSuffix, StringComparison.Ordinal));
    }

    private Task<SubtitleCandidatesModel?> GetCandidatesAsync(HttpClient client, string lessonId) =>
        client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{lessonId}/subtitle-candidates", TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> SelectAsync(HttpClient client, string lessonId, string? trackId) =>
        client.PutAsJsonAsync(
            $"/api/v1/lessons/{lessonId}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(trackId),
            TestContext.Current.CancellationToken);

    private async Task RescanAsync() => await application.EnsureCatalogScannedAsync();

    [Fact]
    public async Task SwitchingBetweenTwoExistingTracksReplacesTheAssociation()
    {
        var client = await GetReadyClientAsync();
        var lesson = await GetLessonAsync(client, "CourseSubs", "video.mp4");
        var candidates = await GetCandidatesAsync(client, lesson.Id);

        var srt = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("video.srt", StringComparison.Ordinal));
        var vtt = candidates.Candidates.Single(candidate => candidate.Label.EndsWith("video.vtt", StringComparison.Ordinal));

        var first = await SelectAsync(client, lesson.Id, srt.Id);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(srt.Id, (await GetCandidatesAsync(client, lesson.Id))!.ManualId);

        // The track id is part of the association's key, so this is the case that used to
        // return 404 and leave the first track selected.
        var second = await SelectAsync(client, lesson.Id, vtt.Id);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var after = await GetCandidatesAsync(client, lesson.Id);
        Assert.Equal(vtt.Id, after!.ManualId);
        Assert.Equal(vtt.Id, after.ResolvedId);

        // Switching back again must work just as well.
        (await SelectAsync(client, lesson.Id, srt.Id)).EnsureSuccessStatusCode();
        Assert.Equal(srt.Id, (await GetCandidatesAsync(client, lesson.Id))!.ManualId);

        (await SelectAsync(client, lesson.Id, null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task EditingASubtitleSourceInvalidatesTheNormalizedCaptions()
    {
        var client = await GetReadyClientAsync();
        var lesson = await GetLessonAsync(client, "CourseAlpha", "02 Getting Started.mp4");
        var candidates = await GetCandidatesAsync(client, lesson.Id);
        var track = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("02 Getting Started.srt", StringComparison.Ordinal));

        var path = Path.Combine(application.FixtureRoot, "CourseAlpha", "01 Intro", "Subtitles", "02 Getting Started.srt");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        try
        {
            var before = await client.GetStringAsync($"/media/subtitles/{track.Id}.vtt", TestContext.Current.CancellationToken);
            Assert.Contains("folder cue", before, StringComparison.Ordinal);

            await File.WriteAllBytesAsync(
                path,
                Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:02,000\nEdited caption text\n"),
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

            // First without rescanning: delivery compares the live file against the identity
            // it normalized, so a file edited between scans cannot keep serving old captions.
            var edited = await client.GetStringAsync($"/media/subtitles/{track.Id}.vtt", TestContext.Current.CancellationToken);
            Assert.Contains("Edited caption text", edited, StringComparison.Ordinal);
            Assert.DoesNotContain("folder cue", edited, StringComparison.Ordinal);

            // Then with a rescan, which rewrites the row's size and modification time. That is
            // the path that used to leave the normalized artifact in place indefinitely.
            await File.WriteAllBytesAsync(
                path,
                Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:03,000\nRescanned caption text\n"),
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
            await RescanAsync();

            var after = await client.GetStringAsync($"/media/subtitles/{track.Id}.vtt", TestContext.Current.CancellationToken);
            Assert.Contains("Rescanned caption text", after, StringComparison.Ordinal);
            Assert.DoesNotContain("Edited caption text", after, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
            await RescanAsync();
        }
    }

    [Fact]
    public async Task UndecodableBytesAreReportedRatherThanReplaced()
    {
        var client = await GetReadyClientAsync();
        var directory = Path.Combine(application.FixtureRoot, "CourseSubs");
        var path = Path.Combine(directory, "video.bad.srt");

        // A lone 0xFF is not valid UTF-8. With a replacement fallback it silently becomes
        // U+FFFD in the caption; SUB-01 requires the file to be reported instead.
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nbroken "));
        bytes.Add(0xFF);
        bytes.AddRange(Encoding.UTF8.GetBytes("\n"));
        await File.WriteAllBytesAsync(path, bytes.ToArray(), TestContext.Current.CancellationToken);

        try
        {
            await RescanAsync();
            var lesson = await GetLessonAsync(client, "CourseSubs", "video.mp4");
            var candidates = await GetCandidatesAsync(client, lesson.Id);
            var track = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("video.bad.srt", StringComparison.Ordinal));

            var response = await client.GetAsync($"/media/subtitles/{track.Id}.vtt", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("not valid UTF-8", problem, StringComparison.Ordinal);

            var afterFailure = await GetCandidatesAsync(client, lesson.Id);
            var failed = afterFailure!.Candidates.Single(candidate => candidate.Id == track.Id);
            Assert.Equal("Failed", failed.State);
            Assert.NotNull(failed.Message);
        }
        finally
        {
            File.Delete(path);
            await RescanAsync();
        }
    }

    [Fact]
    public async Task AManualPreferenceSurvivesItsSidecarDisappearing()
    {
        var client = await GetReadyClientAsync();
        var directory = Path.Combine(application.FixtureRoot, "CourseSubs");
        var path = Path.Combine(directory, "video.keepme.srt");
        await File.WriteAllBytesAsync(
            path,
            Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nkeep me\n"),
            TestContext.Current.CancellationToken);

        try
        {
            await RescanAsync();
            var lesson = await GetLessonAsync(client, "CourseSubs", "video.mp4");
            var candidates = await GetCandidatesAsync(client, lesson.Id);
            var track = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("video.keepme.srt", StringComparison.Ordinal));

            (await SelectAsync(client, lesson.Id, track.Id)).EnsureSuccessStatusCode();

            File.Delete(path);
            await RescanAsync();

            var after = await GetCandidatesAsync(client, lesson.Id);

            // The preference is remembered and explained rather than cascaded away, and no
            // other file is quietly substituted for it.
            Assert.Equal(track.Id, after!.ManualId);
            Assert.Null(after.ResolvedId);
            Assert.NotNull(after.AmbiguityMessage);
            Assert.Contains("no longer in the library", after.AmbiguityMessage!, StringComparison.Ordinal);

            var missing = after.Candidates.Single(candidate => candidate.Id == track.Id);
            Assert.Equal("Missing", missing.State);
            Assert.Null(missing.TrackUrl);
            Assert.False(missing.AutoSelectable);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var lesson = await GetLessonAsync(client, "CourseSubs", "video.mp4");
            await SelectAsync(client, lesson.Id, null);
            await RescanAsync();
        }
    }

    [Fact]
    public async Task ReturningToAutomaticResolvesATrack()
    {
        var client = await GetReadyClientAsync();
        var lesson = await GetLessonAsync(client, "CourseAlpha", "02 Getting Started.mp4");
        var candidates = await GetCandidatesAsync(client, lesson.Id);

        // This lesson has a single subtitles-folder match, so automatic is unambiguous.
        Assert.NotNull(candidates!.AutoSelectedId);
        var other = candidates.Candidates.First(candidate => candidate.Id != candidates.AutoSelectedId);

        (await SelectAsync(client, lesson.Id, other.Id)).EnsureSuccessStatusCode();

        var automatic = await SelectAsync(client, lesson.Id, null);
        automatic.EnsureSuccessStatusCode();
        var selection = await automatic.Content.ReadFromJsonAsync<SubtitleSelectionModel>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(selection!.Automatic);
        Assert.Null(selection.SubtitleTrackId);

        // The selection response carries no track by design; the candidate list is what the
        // player re-reads to keep a text track rendered.
        var after = await GetCandidatesAsync(client, lesson.Id);
        Assert.Null(after!.ManualId);
        Assert.Equal(candidates.AutoSelectedId, after.ResolvedId);

        var manifest = await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{lesson.Id}/playback", TestContext.Current.CancellationToken);
        Assert.Equal(candidates.AutoSelectedId, manifest!.Selection.SelectedSubtitleId);
    }
}
