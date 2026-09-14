using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TutsVideoPlayer.Web.Features.Subtitles;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class SubtitleSelectionTests(TestApplication application)
{
    private static async Task<HttpResponseMessage> PutSubtitlesEnabledAsync(HttpClient client, bool enabled)
    {
        var current = await client.GetFromJsonAsync<SettingsModel>("/api/v1/settings", TestContext.Current.CancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(new SettingsUpdateModel(null, null, null, SubtitleEnabled: enabled))
        };
        request.Headers.IfMatch.ParseAdd($"\"{current!.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

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

    private async Task<CourseTreeModel> GetTreeAsync(HttpClient client, string courseTitle)
    {
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var course = courses!.Courses.Single(candidate => candidate.Title == courseTitle);
        return (await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{course.Id}/tree", TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task AdjacentSubtitlesAreAmbiguousBetweenSrtAndVttUntilChosen()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseSubs");
        var video = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("video.mp4", StringComparison.Ordinal));

        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);

        Assert.NotNull(candidates);
        Assert.Null(candidates!.AutoSelectedId);
        Assert.Contains("More than one subtitle file matches", candidates.AmbiguityMessage!);

        var adjacent = candidates.Candidates.Where(candidate => candidate.Reason == "adjacent file").ToList();
        Assert.Equal(2, adjacent.Count);
        Assert.All(adjacent, candidate => Assert.Equal("adjacent file", candidate.Reason));
        Assert.Contains(adjacent, candidate => candidate.State == "Discovered" && candidate.TrackUrl is not null);
    }

    [Fact]
    public async Task ManualSelectionPersistsThroughRescansAndFeedsTheManifest()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseSubs");
        var video = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("video.mp4", StringComparison.Ordinal));

        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        var vtt = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("video.vtt", StringComparison.Ordinal));

        var select = await client.PutAsJsonAsync($"/api/v1/lessons/{video.Id}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(vtt.Id), TestContext.Current.CancellationToken);
        select.EnsureSuccessStatusCode();

        var manifest = await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{video.Id}/playback", TestContext.Current.CancellationToken);
        Assert.Equal(vtt.Id, manifest!.Selection.SelectedSubtitleId);
        Assert.Contains(manifest.Subtitles, subtitle => subtitle.Id == vtt.Id && subtitle.Reason == "adjacent file");

        var rescanned = await client.StartScanAsync(TestContext.Current.CancellationToken);
        rescanned.EnsureSuccessStatusCode();
        var scanned = await rescanned.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        await TestApplication.WaitForScanCompletionAsync(client, scanned!.ScanRunId);

        var afterRescan = await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{video.Id}/playback", TestContext.Current.CancellationToken);
        Assert.Equal(vtt.Id, afterRescan!.Selection.SelectedSubtitleId);
    }

    [Fact]
    public async Task AutomaticSelectionClearsTheManualAssociation()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseSubs");
        var video = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("video.mp4", StringComparison.Ordinal));

        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        var first = candidates!.Candidates[0];

        await client.PutAsJsonAsync($"/api/v1/lessons/{video.Id}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(first.Id), TestContext.Current.CancellationToken);
        var automatic = await client.PutAsJsonAsync($"/api/v1/lessons/{video.Id}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(null), TestContext.Current.CancellationToken);
        automatic.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        Assert.Null(after!.ManualId);
    }

    [Fact]
    public async Task SelectionOutsideTheCourseIsRefused()
    {
        var client = await GetReadyClientAsync();
        var subsTree = await GetTreeAsync(client, "CourseSubs");
        var subsVideo = subsTree.Nodes.First(node => node.Type == "lesson");
        var otherCourse = await GetTreeAsync(client, "CourseAlpha");
        var otherTrack = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{otherCourse.Nodes.First(node => node.Type == "lesson").Id}/subtitle-candidates",
            TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync($"/api/v1/lessons/{subsVideo.Id}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(otherTrack!.Candidates[0].Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SubtitlesFolderTrackIsAutoSelectedWhenUnique()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseAlpha");
        var lesson = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("02 Getting Started.mp4", StringComparison.Ordinal));

        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{lesson.Id}/subtitle-candidates", TestContext.Current.CancellationToken);

        Assert.NotNull(candidates!.AutoSelectedId);
        Assert.Equal("subtitles folder", candidates.Candidates.Single(candidate => candidate.Id == candidates.AutoSelectedId).Reason);
    }

    [Fact]
    public async Task LanguageSuffixesProduceALabeledTitleMatch()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseAlpha");
        var lesson = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("10 Advanced.mp4", StringComparison.Ordinal));

        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{lesson.Id}/subtitle-candidates", TestContext.Current.CancellationToken);

        var english = candidates!.Candidates.Single(candidate => candidate.Reason == "title match");
        Assert.Equal("EN · 10 Advanced.en.srt", english.Label);
        Assert.Equal("en", english.Language);
        Assert.Equal(english.Id, candidates.AutoSelectedId);
    }

    [Fact]
    public async Task MediaDeliveryServesNormalizedVttWithoutTouchingSourceBytes()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseAlpha");
        var lesson = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("02 Getting Started.mp4", StringComparison.Ordinal));
        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{lesson.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        var track = candidates!.Candidates.Single(candidate => candidate.Reason == "subtitles folder");

        var sourcePath = Path.Combine(application.FixtureRoot, "CourseAlpha", "01 Intro", "Subtitles", "02 Getting Started.srt");
        var before = await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken);
        var beforeHash = Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant();

        var response = await client.GetAsync(track.TrackUrl!, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vtt", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("WEBVTT", body);
        Assert.Contains("00:00:00.000 --> 00:00:01.000", body);
        Assert.Contains("folder cue", body);

        var after = await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken);
        var afterHash = Convert.ToHexString(SHA256.HashData(after)).ToLowerInvariant();
        Assert.Equal(beforeHash, afterHash);
    }

    [Fact]
    public async Task MalformedSubtitlesAreReportedAndRefused()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseSubs");
        var video = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("video.mp4", StringComparison.Ordinal));

        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        var broken = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("broken.srt", StringComparison.Ordinal));
        Assert.NotEqual("Failed", broken.State);

        var refused = await client.GetAsync($"/media/subtitles/{broken.Id}.vtt", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("malformed", problem);

        var after = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        Assert.Equal("Failed", after!.Candidates.Single(candidate => candidate.Id == broken.Id).State);
    }

    [Fact]
    public async Task GlobalOffOverridesSelectionAndSurvivesLessonChanges()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseSubs");
        var video = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("video.mp4", StringComparison.Ordinal));
        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);

        var select = await client.PutAsJsonAsync($"/api/v1/lessons/{video.Id}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(candidates!.Candidates[0].Id), TestContext.Current.CancellationToken);
        select.EnsureSuccessStatusCode();

        var disable = await PutSubtitlesEnabledAsync(client, enabled: false);
        disable.EnsureSuccessStatusCode();

        var manifest = await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{video.Id}/playback", TestContext.Current.CancellationToken);
        Assert.False(manifest!.Selection.SubtitlesEnabled);
        Assert.Null(manifest.Selection.SelectedSubtitleId);

        // The global preference follows the learner to another lesson in the same course.
        var other = tree.Nodes.Where(node => node.Type == "lesson").Last();
        var otherManifest = await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{other.Id}/playback", TestContext.Current.CancellationToken);
        Assert.False(otherManifest!.Selection.SubtitlesEnabled);

        var reEnable = await PutSubtitlesEnabledAsync(client, true);
        reEnable.EnsureSuccessStatusCode();

        var restored = await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{video.Id}/playback", TestContext.Current.CancellationToken);
        Assert.Equal(candidates.Candidates[0].Id, restored!.Selection.SelectedSubtitleId);
    }

    [Fact]
    public async Task MissingPreferredTrackExplainsAndOffersAlternatives()
    {
        var client = await GetReadyClientAsync();
        var tree = await GetTreeAsync(client, "CourseSubs");
        var video = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("video.mp4", StringComparison.Ordinal));
        var candidates = await client.GetFromJsonAsync<SubtitleCandidatesModel>(
            $"/api/v1/lessons/{video.Id}/subtitle-candidates", TestContext.Current.CancellationToken);
        var vtt = candidates!.Candidates.Single(candidate => candidate.Label.EndsWith("video.vtt", StringComparison.Ordinal));

        await client.PutAsJsonAsync($"/api/v1/lessons/{video.Id}/subtitle-selection",
            new SubtitleSelectionController.SelectionRequest(vtt.Id), TestContext.Current.CancellationToken);

        // Delete the underlying file without rescanning: the association still points at a
        // track whose source can no longer be resolved. The shared fixture file is restored
        // so later tests still see the complete library.
        var vttPath = Path.Combine(application.FixtureRoot, "CourseSubs", "video.vtt");
        var vttContent = await File.ReadAllBytesAsync(vttPath, TestContext.Current.CancellationToken);
        File.Delete(vttPath);

        try
        {
            var refused = await client.GetAsync($"/media/subtitles/{vtt.Id}.vtt", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("not available inside the library root", problem);
        }
        finally
        {
            await File.WriteAllBytesAsync(vttPath, vttContent, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task UnknownTrackReturnsNotFound()
    {
        var client = await GetReadyClientAsync();

        var response = await client.GetAsync("/media/subtitles/999999.vtt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}