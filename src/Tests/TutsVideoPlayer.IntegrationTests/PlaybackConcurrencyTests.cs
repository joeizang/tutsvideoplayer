using System.Net;
using System.Net.Http.Json;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Overlapping writes share the progress revision, which is an EF concurrency token. They must
/// resolve through the documented session/sequence protocol instead of failing the request.
/// </summary>
[Collection(TestApplication.CollectionName)]
public class PlaybackConcurrencyTests(TestApplication application)
{
    private HttpClient CreateGuardedClient()
    {
        var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.RequestHeaderName, "1");
        return client;
    }

    private async Task<string> FirstAlphaLessonIdAsync(HttpClient client)
    {
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var alpha = courses!.Courses.Single(course => course.Title == "CourseAlpha");
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{alpha.Id}/tree", TestContext.Current.CancellationToken);
        return tree!.Nodes.First(node => node.Type == "lesson").Id;
    }

    [Fact]
    public async Task ConcurrentProgressWritesNeverFailWithAServerError()
    {
        var client = CreateGuardedClient();
        await application.EnsureCatalogScannedAsync();
        var lessonId = await FirstAlphaLessonIdAsync(client);

        var startResponse = await client.PostAsync($"/api/v1/lessons/{lessonId}/playback-sessions", null, TestContext.Current.CancellationToken);
        startResponse.EnsureSuccessStatusCode();
        var session = (await startResponse.Content.ReadFromJsonAsync<PlaybackSessionStartModel>(
            cancellationToken: TestContext.Current.CancellationToken))!;

        var writes = Enumerable.Range(1, 12).Select(offset =>
            client.PutAsJsonAsync(
                $"/api/v1/playback-sessions/{session.SessionId}/progress",
                new ProgressWriteModel(session.LastSequence + offset, 1, 1_000 * offset, IsPlaying: true, Ended: false),
                TestContext.Current.CancellationToken));

        var responses = await Task.WhenAll(writes);

        foreach (var response in responses)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"A concurrent progress write returned {(int)response.StatusCode} {response.StatusCode}.");
        }

        // At least one writer has to win outright; nothing is allowed to surface as a 500.
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task ConcurrentCompletionAndSettingsWritesResolveAsConflictsNotFailures()
    {
        var client = CreateGuardedClient();
        await application.EnsureCatalogScannedAsync();
        var lessonId = await FirstAlphaLessonIdAsync(client);

        var current = (await client.GetFromJsonAsync<CompletionResultModel>(
            $"/api/v1/lessons/{lessonId}/completion", TestContext.Current.CancellationToken))!;

        // Both requests carry the same revision, so exactly one of them can win.
        var attempts = new[] { "Completed", "Incomplete" }.Select(choice =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/lessons/{lessonId}/completion")
            {
                Content = JsonContent.Create(new CompletionRequestModel(choice))
            };
            request.Headers.IfMatch.ParseAdd($"\"{current.Revision}\"");
            return client.SendAsync(request, TestContext.Current.CancellationToken);
        });

        var responses = await Task.WhenAll(attempts);

        foreach (var response in responses)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict,
                $"A concurrent completion write returned {(int)response.StatusCode} {response.StatusCode}.");
        }

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);

        // Restore the shared lesson's default for the rest of the collection.
        var latest = (await client.GetFromJsonAsync<CompletionResultModel>(
            $"/api/v1/lessons/{lessonId}/completion", TestContext.Current.CancellationToken))!;
        var reset = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/lessons/{lessonId}/completion")
        {
            Content = JsonContent.Create(new CompletionRequestModel("Automatic"))
        };
        reset.Headers.IfMatch.ParseAdd($"\"{latest.Revision}\"");
        (await client.SendAsync(reset, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ABeaconShapedCloseIsRejectedSoTheClientMustUseKeepaliveFetch()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var guarded = CreateGuardedClient();
        var lessonId = await FirstAlphaLessonIdAsync(guarded);
        var startResponse = await guarded.PostAsync($"/api/v1/lessons/{lessonId}/playback-sessions", null, TestContext.Current.CancellationToken);
        var session = (await startResponse.Content.ReadFromJsonAsync<PlaybackSessionStartModel>(
            cancellationToken: TestContext.Current.CancellationToken))!;

        // navigator.sendBeacon cannot set the custom same-origin header, which is why the
        // navigation flush has to be a keepalive fetch instead.
        var beacon = await client.PostAsJsonAsync(
            $"/api/v1/playback-sessions/{session.SessionId}/close",
            new SessionCloseModel(session.LastSequence + 1, 42_000),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, beacon.StatusCode);

        var keepalive = await guarded.PostAsJsonAsync(
            $"/api/v1/playback-sessions/{session.SessionId}/close",
            new SessionCloseModel(session.LastSequence + 1, 42_000),
            TestContext.Current.CancellationToken);
        keepalive.EnsureSuccessStatusCode();
        var result = await keepalive.Content.ReadFromJsonAsync<SessionCloseResultModel>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result!.ProgressFlushed);

        // Repeating the flush is safe, which is what makes a lifecycle hook usable.
        var repeated = await guarded.PostAsJsonAsync(
            $"/api/v1/playback-sessions/{session.SessionId}/close",
            new SessionCloseModel(session.LastSequence + 2, 43_000),
            TestContext.Current.CancellationToken);
        repeated.EnsureSuccessStatusCode();
        Assert.False((await repeated.Content.ReadFromJsonAsync<SessionCloseResultModel>(
            cancellationToken: TestContext.Current.CancellationToken))!.ProgressFlushed);
    }
}
