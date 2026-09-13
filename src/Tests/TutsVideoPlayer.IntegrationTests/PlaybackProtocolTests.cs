using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class PlaybackProtocolTests(TestApplication application)
{
    private static HttpClient CreateGuardedClient(TestApplication application)
    {
        var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.RequestHeaderName, "1");
        return client;
    }

    private async Task<CourseTreeModel> GetAlphaTreeAsync(HttpClient client)
    {
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var alpha = courses!.Courses.Single(course => course.Title == "CourseAlpha");
        return (await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{alpha.Id}/tree", TestContext.Current.CancellationToken))!;
    }

    private async Task<LessonDetailModel> GetLessonAsync(HttpClient client, string lessonId) =>
        (await client.GetFromJsonAsync<LessonDetailModel>($"/api/v1/lessons/{lessonId}", TestContext.Current.CancellationToken))!;

    private async Task<PlaybackManifestModel> GetManifestAsync(HttpClient client, string lessonId) =>
        (await client.GetFromJsonAsync<PlaybackManifestModel>($"/api/v1/lessons/{lessonId}/playback", TestContext.Current.CancellationToken))!;

    private async Task<PlaybackSessionStartModel> StartSessionAsync(HttpClient client, string lessonId)
    {
        var response = await client.PostAsync($"/api/v1/lessons/{lessonId}/playback-sessions", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaybackSessionStartModel>(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> WriteProgressAsync(HttpClient client, string sessionId, ProgressWriteModel command)
    {
        return await client.PutAsJsonAsync($"/api/v1/playback-sessions/{sessionId}/progress", command, TestContext.Current.CancellationToken);
    }

    private static long RealLessonDurationMs(PlaybackManifestModel manifest) => manifest.DurationMs ?? 0;

    [Fact]
    public async Task SessionStartReturnsSavedPositionAndSequenceContinuation()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");

        var first = await StartSessionAsync(client, lesson.Id);
        var write = await WriteProgressAsync(client, first.SessionId, new ProgressWriteModel(
            first.LastSequence + 1, 1, PositionMs: 25_000, IsPlaying: true, Ended: false));
        write.EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/v1/playback-sessions/{first.SessionId}/close", new SessionCloseModel(null, null), TestContext.Current.CancellationToken);

        var second = await StartSessionAsync(client, lesson.Id);

        Assert.Equal(25_000, second.SavedPositionMs);
        Assert.True(second.LastSequence >= first.LastSequence + 1);
    }

    [Fact]
    public async Task ProgressAcceptsMonotonicSequenceAcknowledgesDuplicatesAndRejectsRegression()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");
        var session = await StartSessionAsync(client, lesson.Id);

        var accepted = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 1, 1, 4_000, IsPlaying: true, Ended: false));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<ProgressWriteResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4_000, acceptedBody!.AcceptedPositionMs);

        var duplicate = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 1, 1, 4_000, IsPlaying: true, Ended: false));
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<ProgressWriteResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(duplicateBody!.Duplicate);

        var regression = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence - 3, 1, 1_000, IsPlaying: true, Ended: false));
        Assert.Equal(HttpStatusCode.Conflict, regression.StatusCode);
    }

    [Fact]
    public async Task BackwardSeekPersistsTheLowerPosition()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");
        var session = await StartSessionAsync(client, lesson.Id);

        await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 1, 1, 30_000, IsPlaying: true, Ended: false));
        var backward = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 2, 1, 5_000, IsPlaying: true, Ended: false));

        var body = await backward.Content.ReadFromJsonAsync<ProgressWriteResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5_000, body!.AcceptedPositionMs);

        var manifest = await GetManifestAsync(client, lesson.Id);
        Assert.Equal(5_000, manifest.Progress.PositionMs);
    }

    [Fact]
    public async Task EndedEventMarksTheLessonEffectivelyComplete()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");
        var session = await StartSessionAsync(client, lesson.Id);

        var ended = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 1, 1, 60_000, IsPlaying: true, Ended: true));

        var body = await ended.Content.ReadFromJsonAsync<ProgressWriteResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(body!.EffectiveCompletion);

        var manifest = await GetManifestAsync(client, lesson.Id);
        Assert.True(manifest.Progress.EffectiveCompletion);
    }

    [Fact]
    public async Task PlaybackPastTheCompletionThresholdCompletesWhilePlaying()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var realCourse = courses!.Courses.Single(course => course.Title == "RealCourse");
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{realCourse.Id}/tree", TestContext.Current.CancellationToken);
        var lesson = tree!.Nodes.First(node => node.Type == "lesson");
        var manifest = await GetManifestAsync(client, lesson.Id);
        Assert.True(manifest.DurationMs is > 0, "The real fixture must report a duration.");
        var session = await StartSessionAsync(client, lesson.Id);

        var thresholdPosition = (long)(manifest.DurationMs!.Value * 0.96);
        var write = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 1, manifest.SourceGeneration, thresholdPosition, IsPlaying: true, Ended: false));

        var body = await write.Content.ReadFromJsonAsync<ProgressWriteResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(body!.EffectiveCompletion);
    }

    [Fact]
    public async Task StaleSessionWritesAreRejectedAndCloseDoesNotOverwrite()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");

        var sessionA = await StartSessionAsync(client, lesson.Id);
        var sessionB = await StartSessionAsync(client, lesson.Id);

        var staleWrite = await WriteProgressAsync(client, sessionA.SessionId, new ProgressWriteModel(
            sessionA.LastSequence + 1, 1, 99_000, IsPlaying: true, Ended: false));
        Assert.Equal(HttpStatusCode.Conflict, staleWrite.StatusCode);

        var currentWrite = await WriteProgressAsync(client, sessionB.SessionId, new ProgressWriteModel(
            sessionB.LastSequence + 1, 1, 12_000, IsPlaying: true, Ended: false));
        currentWrite.EnsureSuccessStatusCode();

        var staleClose = await client.PostAsJsonAsync($"/api/v1/playback-sessions/{sessionA.SessionId}/close",
            new SessionCloseModel(sessionA.LastSequence + 2, 100_000), TestContext.Current.CancellationToken);
        staleClose.EnsureSuccessStatusCode();

        var manifest = await GetManifestAsync(client, lesson.Id);
        Assert.Equal(12_000, manifest.Progress.PositionMs);
    }

    [Fact]
    public async Task ManualIncompleteSurvivesAutomaticEndedSignals()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");
        var session = await StartSessionAsync(client, lesson.Id);

        var incomplete = await client.PutAsJsonAsync($"/api/v1/lessons/{lesson.Id}/completion",
            new CompletionRequestModel("Incomplete"), TestContext.Current.CancellationToken);
        incomplete.EnsureSuccessStatusCode();

        var ended = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
            session.LastSequence + 1, 1, 60_000, IsPlaying: true, Ended: true));
        ended.EnsureSuccessStatusCode();

        var manifest = await GetManifestAsync(client, lesson.Id);
        Assert.Equal("Incomplete", manifest.Progress.ManualCompletion);
        Assert.False(manifest.Progress.EffectiveCompletion);
    }

    [Fact]
    public async Task CompletionUpdatesRequireCurrentRevision()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");

        var first = await client.PutAsJsonAsync($"/api/v1/lessons/{lesson.Id}/completion",
            new CompletionRequestModel("Completed"), TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();

        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/lessons/{lesson.Id}/completion")
        {
            Content = JsonContent.Create(new CompletionRequestModel("Incomplete"))
        };
        stale.Headers.IfMatch.ParseAdd("\"0\"");
        var staleResponse = await client.SendAsync(stale, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        Assert.NotNull(staleResponse.Headers.ETag);

        var fresh = await client.PutAsJsonAsync($"/api/v1/lessons/{lesson.Id}/completion",
            new CompletionRequestModel("Automatic"), TestContext.Current.CancellationToken);
        fresh.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ContinueLearningRecommendsNextThenReplay()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var tree = await GetAlphaTreeAsync(client);
        var lessons = tree.Nodes.Where(node => node.Type == "lesson").ToList();

        foreach (var lesson in lessons)
        {
            var session = await StartSessionAsync(client, lesson.Id);
            var write = await WriteProgressAsync(client, session.SessionId, new ProgressWriteModel(
                session.LastSequence + 1, 1, 1_000, IsPlaying: true, Ended: true));
            write.EnsureSuccessStatusCode();
        }

        var continueEntries = await client.GetFromJsonAsync<IReadOnlyList<ContinueLearningEntryModel>>(
            "/api/v1/learning/continue", TestContext.Current.CancellationToken);
        var alpha = continueEntries!.Single(entry => entry.CourseTitle == "CourseAlpha");

        Assert.Equal("replay", alpha.Recommendation);
        Assert.Equal(lessons.Count, alpha.CompletedLessons);
        Assert.Equal(lessons[0].Id, alpha.RecommendedLessonId);
    }

    [Fact]
    public async Task SettingsRoundTripValidatesAndFeedsTheManifest()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();

        var defaults = await client.GetFromJsonAsync<SettingsModel>("/api/v1/settings", TestContext.Current.CancellationToken);
        Assert.Equal(1, defaults!.PlaybackSpeed);
        Assert.False(defaults.Autoplay);

        var update = await client.PutAsJsonAsync("/api/v1/settings",
            new SettingsUpdateModel(PlaybackSpeed: 1.5, Autoplay: true, FitMode: "Fill", SubtitleEnabled: null),
            TestContext.Current.CancellationToken);
        update.EnsureSuccessStatusCode();

        var invalid = await client.PutAsJsonAsync("/api/v1/settings",
            new SettingsUpdateModel(PlaybackSpeed: 1.3, null, null, null),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var lesson = (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson");
        var manifest = await GetManifestAsync(client, lesson.Id);
        Assert.Equal(1.5, manifest.Preferences.Speed);
        Assert.True(manifest.Preferences.Autoplay);
        Assert.Equal("Fill", manifest.Preferences.FitMode);
    }

    [Fact]
    public async Task MediaRenditionsDeliverRangesForReadySourcesOnly()
    {
        var client = CreateGuardedClient(application);
        await application.EnsureCatalogScannedAsync();
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var realCourse = courses!.Courses.Single(course => course.Title == "RealCourse");
        var manifest = await GetManifestAsync(client,
            (await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{realCourse.Id}/tree", TestContext.Current.CancellationToken))!
                .Nodes.First(node => node.Type == "lesson").Id);

        var ready = manifest.Renditions.Single(candidate => candidate.State == "Ready");
        Assert.NotNull(ready.MediaUrl);
        Assert.Equal("video/mp4", ready.MimeType);

        var full = await client.GetAsync(ready.MediaUrl!, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("bytes", full.Headers.AcceptRanges.First());
        var total = full.Content.Headers.ContentLength ?? 0;
        Assert.True(total > 0);

        var range = new HttpRequestMessage(HttpMethod.Get, ready.MediaUrl);
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
        var partial = await client.SendAsync(range, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal($"bytes 0-99/{total}", partial.Content.Headers.ContentRange?.ToString());

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, ready.MediaUrl), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.True((head.Content.Headers.ContentLength ?? 0) > 0);

        var outOfRange = new HttpRequestMessage(HttpMethod.Get, ready.MediaUrl);
        outOfRange.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(999_999_999, null);
        var unsatisfiable = await client.SendAsync(outOfRange, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);

        var staleManifest = await GetManifestAsync(client,
            (await GetAlphaTreeAsync(client)).Nodes.First(node => node.Type == "lesson").Id);
        var stale = staleManifest.Renditions.Single(candidate => candidate.State != "Ready");
        Assert.Null(stale.MediaUrl);
        var staleResponse = await client.GetAsync($"/media/renditions/{stale.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        var unknown = await client.GetAsync("/media/renditions/999999", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}