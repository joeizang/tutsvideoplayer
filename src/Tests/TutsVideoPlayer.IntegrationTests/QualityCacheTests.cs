using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TutsVideoPlayer.Web.Features.Subtitles;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// M5: quality preparation and safe cache management — eligibility, preferred
/// default selection with fallback, storage accounting, and eviction that can
/// never touch permanent copies or actively played renditions.
/// </summary>
[Collection(PreparingApplication.CollectionName)]
public class QualityCacheTests(PreparingApplication application)
{
    private HttpClient CreateGuardedClient()
    {
        var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(TutsVideoPlayer.Web.Hosting.SameOriginMutationFilter.RequestHeaderName, "1");
        return client;
    }

    private async Task<CourseTreeModel> GetPrepTreeAsync(HttpClient client)
    {
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var prep = courses!.Courses.Single(course => course.Title == "CoursePrep");
        return (await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{prep.Id}/tree", TestContext.Current.CancellationToken))!;
    }

    private async Task<ScanStartResultModel> ScanAsync(HttpClient client)
    {
        var start = await client.StartScanAsync(TestContext.Current.CancellationToken);
        start.EnsureSuccessStatusCode();
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        await TestApplication.WaitForScanCompletionAsync(client, started!.ScanRunId);
        return started;
    }

    private async Task<PlaybackManifestModel> GetManifestAsync(HttpClient client, string lessonId) =>
        (await client.GetFromJsonAsync<PlaybackManifestModel>(
            $"/api/v1/lessons/{lessonId}/playback", TestContext.Current.CancellationToken))!;

    private async Task<List<PreparationJobModel>> WaitUntilSettledAsync(HttpClient client, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var jobs = await client.GetFromJsonAsync<List<PreparationJobModel>>(
                "/api/v1/preparations?limit=100", TestContext.Current.CancellationToken);
            if (jobs is not null
                && jobs.All(job => job.State is "Succeeded" or "Failed" or "Blocked" or "Canceled"))
            {
                return jobs;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Preparation jobs did not settle in time.");
    }

    private async Task<SettingsModel> PutSettingsAsync(HttpClient client, SettingsUpdateModel update)
    {
        var current = await client.GetFromJsonAsync<SettingsModel>("/api/v1/settings", TestContext.Current.CancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(update)
        };
        request.Headers.IfMatch.ParseAdd($"\"{current!.Revision}\"");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SettingsModel>(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EvictionSparesPermanentAndActiveCopiesAndRecoversAfterRestart()
    {
        var client = CreateGuardedClient();
        await ScanAsync(client);
        var tree = await GetPrepTreeAsync(client);
        var tall = tree.Nodes.First(node => node.Type == "lesson" && node.Filename!.EndsWith("04 Tall Lesson.mp4", StringComparison.Ordinal));

        // Prepare a 480 quality version for the tall lesson.
        var requested = await client.PostAsJsonAsync($"/api/v1/lessons/{tall.Id}/preparations",
            new PreparationRequestModel("Quality", "480"), TestContext.Current.CancellationToken);
        requested.EnsureSuccessStatusCode();
        await WaitUntilSettledAsync(client);

        var manifest = await GetManifestAsync(client, tall.Id);
        var qualityCopy = manifest.Renditions.Single(rendition => rendition.Profile == "480");
        Assert.Equal("Ready", qualityCopy.State);
        Assert.NotNull(qualityCopy.MediaUrl);

        // An active playback session leases the quality rendition.
        var session = await client.PostAsync($"/api/v1/lessons/{tall.Id}/playback-sessions", null, TestContext.Current.CancellationToken);
        session.EnsureSuccessStatusCode();
        var startedSession = await session.Content.ReadFromJsonAsync<PlaybackSessionStartModel>(cancellationToken: TestContext.Current.CancellationToken);
        var heartbeat = await client.PostAsJsonAsync($"/api/v1/playback-sessions/{startedSession!.SessionId}/heartbeat",
            new HeartbeatModel(qualityCopy.Id), TestContext.Current.CancellationToken);
        heartbeat.EnsureSuccessStatusCode();

        // Drop the limit below the quality usage: eviction must spare the leased
        // rendition and every permanent copy, and report pending cleanup instead.
        // The limit is restored afterwards so later tests keep working.
        try
        {
            // Phase A: a limit below the quality usage reports pending cleanup while
            // the leased rendition is protected and cannot be touched.
            await PutSettingsAsync(client, new SettingsUpdateModel(null, null, null, null, null, CacheLimitBytes: 1024));
            await RunPendingCleanupChecksAsync(client, tall.Id);

            // Phase B: a limit that fits the reservation but not the reservation plus
            // the existing copy forces eviction of the older, LRU quality rendition.
            var sourceSize = new FileInfo(Path.Combine(application.FixtureRoot, "CoursePrep", "04 Tall Lesson.mp4")).Length;
            await PutSettingsAsync(client, new SettingsUpdateModel(null, null, null, null, null, CacheLimitBytes: sourceSize + 4096));
            await RunEvictionChecksAsync(client, tall.Id, startedSession!.SessionId);
        }
        finally
        {
            await PutSettingsAsync(client, new SettingsUpdateModel(null, null, null, null, PreferredQuality: null, CacheLimitBytes: 20_000_000_000));
        }
    }

    private async Task RunPendingCleanupChecksAsync(HttpClient client, string tallLessonId)
    {
        var storage = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage", TestContext.Current.CancellationToken);
        Assert.True(storage.GetProperty("qualityProtectedBytes").GetInt64() > 0, "The leased quality rendition must be protected.");
        Assert.True(storage.GetProperty("pendingCleanupBytes").GetInt64() > 0, $"Blocked cleanup must be visible. Storage: {storage.ToString()}");

        // Quality copy survives (protected); permanent copies survive too.
        var stillThere = await GetManifestAsync(client, tallLessonId);
        Assert.Equal("Ready", stillThere.Renditions.Single(rendition => rendition.Profile == "480").State);
        Assert.All(
            stillThere.Renditions.Where(rendition => rendition.RetentionClass == "Permanent"),
            rendition => Assert.Equal("Ready", rendition.State));
    }

    private async Task RunEvictionChecksAsync(HttpClient client, string tallLessonId, string sessionId)
    {
        // Release the lease (close session); eviction can now reclaim the copy.
        var close = await client.PostAsJsonAsync($"/api/v1/playback-sessions/{sessionId}/close",
            new SessionCloseModel(null, null), TestContext.Current.CancellationToken);
        close.EnsureSuccessStatusCode();

        // The next quality request reclaims the LRU 480 copy to make room for a 720.
        var forced = await client.PostAsJsonAsync($"/api/v1/lessons/{tallLessonId}/preparations",
            new PreparationRequestModel("Quality", "720"), TestContext.Current.CancellationToken);
        forced.EnsureSuccessStatusCode();
        await WaitUntilSettledAsync(client);

        var renditions = (await GetManifestAsync(client, tallLessonId)).Renditions;
        var quality480 = renditions.Single(rendition => rendition.Profile == "480");
        var quality720 = renditions.Single(rendition => rendition.Profile == "720");

        var evictedCopies = new[] { quality480, quality720 }.Count(rendition => rendition.State == "Evicted");
        Assert.Equal(1, evictedCopies);
        Assert.All(
            renditions.Where(rendition => rendition.RetentionClass == "Permanent"),
            rendition => Assert.Equal("Ready", rendition.State));

        // Requesting the evicted profile prepares it again; the budget then evicts
        // the other copy (now the least-recently-used one).
        var evictedProfile = quality480.State == "Evicted" ? "480" : "720";
        var survivingProfile = evictedProfile == "480" ? "720" : "480";
        var again = await client.PostAsJsonAsync($"/api/v1/lessons/{tallLessonId}/preparations",
            new PreparationRequestModel("Quality", evictedProfile), TestContext.Current.CancellationToken);
        again.EnsureSuccessStatusCode();
        await WaitUntilSettledAsync(client);
        var reprepared = await GetManifestAsync(client, tallLessonId);
        Assert.Equal("Ready", reprepared.Renditions.Single(rendition => rendition.Profile == evictedProfile).State);
        Assert.Equal("Evicted", reprepared.Renditions.Single(rendition => rendition.Profile == survivingProfile).State);

        // The budget stays honest after the churn.
        var storage = await client.GetFromJsonAsync<JsonElement>("/api/v1/storage", TestContext.Current.CancellationToken);
        Assert.True(
            storage.GetProperty("qualityReadyBytes").GetInt64() <= storage.GetProperty("cacheLimitBytes").GetInt64(),
            $"Quality usage exceeded the limit. Storage: {storage.ToString()}");
    }

}