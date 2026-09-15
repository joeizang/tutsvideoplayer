using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[CollectionDefinition(PreparingApplication.CollectionName)]
public sealed class PreparingWebApplicationCollection : ICollectionFixture<PreparingApplication>
{
}

/// <summary>
/// A dedicated application fixture whose library contains lessons that require
/// real preparation (WMV encode, split TS/AAC mapping) plus an exempt MP4, and
/// whose worker actually runs against ffmpeg.
/// </summary>
public sealed class PreparingApplication : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string CollectionName = "TutsVideoPlayerPreparationApplication";

    public string FixtureRoot { get; } = Directory.CreateTempSubdirectory("tuts-preparing-").FullName;

    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("tuts-prep-appdata-").FullName;

    public async ValueTask InitializeAsync()
    {
        PreparationFixtureBuilder.Build(FixtureRoot);
        var databasePath = CatalogRegistration.ResolvePath(DataDirectory, "tutsvideoplayer.db");
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();
        FixtureTables = string.Join(",", await context.Database.SqlQuery<string>(
            $"SELECT name AS Value FROM sqlite_master WHERE type = 'table' ORDER BY name").ToListAsync());
        FixtureMigrations = string.Join(",", applied);
    }

    public string FixtureTables { get; private set; } = string.Empty;

    public string FixtureMigrations { get; private set; } = string.Empty;

    public async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(FixtureRoot, recursive: true);
        Directory.Delete(DataDirectory, recursive: true);
    }

    public HttpClient CreateGuardedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(SameOriginMutationFilter.RequestHeaderName, "1");
        return client;
    }

    public List<string> CapturedErrors { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("App:LibraryRoot", FixtureRoot);
        builder.UseSetting("App:DataDirectory", DataDirectory);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new ErrorCapture(CapturedErrors));
        });
    }
}

public sealed class ErrorCapture(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Capture(sink);

    public void Dispose() { }

    private sealed class Capture(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error)
            {
                return;
            }

            lock (sink)
            {
                sink.Add($"{formatter(state, exception)} {exception?.Message}");
            }
        }
    }
}

[Collection(PreparingApplication.CollectionName)]
public class PreparationPipelineTests(PreparingApplication application)
{
    private static async Task<JsonDocument> GetQueueSummaryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/preparations/queue", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<List<PreparationJobModel>> WaitUntilSettledAsync(HttpClient client, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        List<PreparationJobModel>? jobs = null;
        while (DateTime.UtcNow < deadline)
        {
            jobs = await client.GetFromJsonAsync<List<PreparationJobModel>>(
                "/api/v1/preparations?limit=50", TestContext.Current.CancellationToken);
            if (jobs is not null
                && jobs.All(job => job.State is "Succeeded" or "Failed" or "Blocked" or "Canceled"))
            {
                return jobs;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        var states = string.Join(", ", (jobs ?? []).Select(job => $"{job.LessonTitle}:{job.State}:{job.ErrorCode}:{job.UserMessage}"));
        throw new TimeoutException($"Preparation jobs did not settle in time. Final states: {states} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(4))}]");
    }

    [Fact]
    public async Task ScanSchedulesJobsOnlyForNonExemptContainers()
    {
        var client = application.CreateGuardedClient();

        // Let the worker's startup recovery and readiness checks settle before
        // driving the API, so the test observes the post-boot state.
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        if (!health.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"Ready check failed ({health.StatusCode}): {await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(4))}] Fixture tables: [{application.FixtureTables}] migrations: [{application.FixtureMigrations}] db: [{application.DataDirectory}]");
        }

        var start = await client.StartScanAsync(TestContext.Current.CancellationToken);
        if (!start.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"StartScan returned {start.StatusCode}: {await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        }
        start.EnsureSuccessStatusCode();
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        await TestApplication.WaitForScanCompletionAsync(client, started!.ScanRunId);

        var listResponse = await client.GetAsync("/api/v1/preparations?limit=50", TestContext.Current.CancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"GET preparations returned {listResponse.StatusCode}: {await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        }

        var jobs = await listResponse.Content.ReadFromJsonAsync<List<PreparationJobModel>>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(jobs);
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.NotEqual("Exempt", job.LessonTitle));
        Assert.DoesNotContain(jobs, job => job.LessonTitle.Contains("Exempt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerPreparesWmvAndSplitTsSourcesWithOriginalBytesPreserved()
    {
        var client = application.CreateGuardedClient();

        await TestApplication.WaitForScanCompletionAsync(client, (await (await client.StartScanAsync(TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken))!.ScanRunId);

        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var prep = courses!.Courses.FirstOrDefault(course => course.Title == "CoursePrep")
            ?? throw new Xunit.Sdk.XunitException(
                $"CoursePrep missing. Visible: [{string.Join(", ", courses.Courses.Select(candidate => candidate.Title))}] Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{prep.Id}/tree", TestContext.Current.CancellationToken);
        var lessons = tree!.Nodes.Where(node => node.Type == "lesson").ToList();

        // Only the non-exempt lessons receive permanent playback copies.
        var preparedLessons = lessons.Where(lesson => !lesson.Filename!.EndsWith("Exempt Lesson.mp4", StringComparison.Ordinal)).ToList();

        var sourceHashes = preparedLessons.ToDictionary(
            lesson => lesson.Filename!,
            lesson => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(application.FixtureRoot, lesson.Filename!)))).ToLowerInvariant());

        var jobs = await WaitUntilSettledAsync(client);
        Assert.All(jobs, job => Assert.Equal("Succeeded", job.State));

        // Diagnostic: renditions BEFORE the rescan.
        var preRescan = new List<string>();
        foreach (var lesson in lessons)
        {
            var manifestPre = await client.GetFromJsonAsync<PlaybackManifestModel>(
                $"/api/v1/lessons/{lesson.Id}/playback", TestContext.Current.CancellationToken);
            preRescan.Add($"{lesson.Id}: [{string.Join(", ", manifestPre!.Renditions.Select(candidate => $"{candidate.Label}:{candidate.State}:{candidate.RetentionClass}"))}]");
        }

        // Rescans must not duplicate lessons or jobs for managed outputs.
        var rescanned = await client.StartScanAsync(TestContext.Current.CancellationToken);
        rescanned.EnsureSuccessStatusCode();
        var scanStatus = (await rescanned.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken))!;
        await TestApplication.WaitForScanCompletionAsync(client, scanStatus.ScanRunId);

        var coursesAfter = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var prepAfter = coursesAfter!.Courses.Single(course => course.Title == "CoursePrep");
        Assert.Equal(prep.LessonCount, prepAfter.LessonCount);
        Assert.Equal(0, prepAfter.MissingLessonCount);

        var jobsAfter = await client.GetFromJsonAsync<List<PreparationJobModel>>(
            "/api/v1/preparations?limit=50", TestContext.Current.CancellationToken);
        if (jobsAfter!.Count != 2)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected 2 jobs, got {jobsAfter.Count}: [{string.Join(", ", jobsAfter.Select(job => $"{job.LessonTitle}:{job.State}"))}] Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        }

        foreach (var lesson in preparedLessons)
        {
            var manifest = await client.GetFromJsonAsync<PlaybackManifestModel>(
                $"/api/v1/lessons/{lesson.Id}/playback", TestContext.Current.CancellationToken);
            var copy = manifest!.Renditions.FirstOrDefault(candidate => candidate.RetentionClass == "Permanent")
                ?? throw new Xunit.Sdk.XunitException(
                    $"No permanent rendition for lesson {lesson.Id}. Pre-rescan: [{string.Join(" || ", preRescan)}]. Post-rescan renditions: [{string.Join(", ", manifest.Renditions.Select(candidate => $"{candidate.Label}:{candidate.State}:{candidate.RetentionClass}"))}]");
            Assert.Equal("Ready", copy.State);
            Assert.NotNull(copy.MediaUrl);
            Assert.True(copy.Height is > 0);

            var media = await client.GetAsync(copy.MediaUrl!, TestContext.Current.CancellationToken);
            media.EnsureSuccessStatusCode();

            var afterHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(application.FixtureRoot, lesson.Filename!)))).ToLowerInvariant();
            Assert.Equal(sourceHashes[lesson.Filename!], afterHash);
        }
    }

    [Fact]
    public async Task QueuePauseStopsClaimsWhileAnnouncingTheSemantics()
    {
        var client = application.CreateGuardedClient();

        // Let the worker's startup recovery and readiness checks settle before
        // driving the API, so the test observes the post-boot state.
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        if (!health.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"Ready check failed ({health.StatusCode}): {await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(4))}]");
        }

        var start = await client.StartScanAsync(TestContext.Current.CancellationToken);
        if (!start.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"StartScan returned {start.StatusCode}: {await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        }
        start.EnsureSuccessStatusCode();
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        await TestApplication.WaitForScanCompletionAsync(client, started!.ScanRunId);

        var settings = await client.GetFromJsonAsync<SettingsModel>("/api/v1/settings", TestContext.Current.CancellationToken);
        var pause = new HttpRequestMessage(HttpMethod.Put, "/api/v1/preparations/queue")
        {
            Content = JsonContent.Create(new QueuePauseModel(Paused: true))
        };
        pause.Headers.IfMatch.ParseAdd($"\"{settings!.Revision}\"");
        var pauseResponse = await client.SendAsync(pause, TestContext.Current.CancellationToken);
        pauseResponse.EnsureSuccessStatusCode();

        var summary = await GetQueueSummaryAsync(client);
        Assert.True(summary.RootElement.GetProperty("paused").GetBoolean());

        // Missing If-Match is a precondition failure, never a silent overwrite.
        var withoutHeader = await client.PutAsJsonAsync("/api/v1/preparations/queue",
            new QueuePauseModel(Paused: false), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutHeader.StatusCode);

        var resume = await client.GetFromJsonAsync<SettingsModel>("/api/v1/settings", TestContext.Current.CancellationToken);
        var resumeRequest = new HttpRequestMessage(HttpMethod.Put, "/api/v1/preparations/queue")
        {
            Content = JsonContent.Create(new QueuePauseModel(Paused: false))
        };
        resumeRequest.Headers.IfMatch.ParseAdd($"\"{resume!.Revision}\"");
        var resumeResponse = await client.SendAsync(resumeRequest, TestContext.Current.CancellationToken);
        resumeResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DuplicatePreparationRequestsConvergeOnOneJob()
    {
        var client = application.CreateGuardedClient();

        // Let the worker's startup recovery and readiness checks settle before
        // driving the API, so the test observes the post-boot state.
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        if (!health.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"Ready check failed ({health.StatusCode}): {await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(4))}]");
        }

        var start = await client.StartScanAsync(TestContext.Current.CancellationToken);
        if (!start.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"StartScan returned {start.StatusCode}: {await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        }
        start.EnsureSuccessStatusCode();
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        await TestApplication.WaitForScanCompletionAsync(client, started!.ScanRunId);

        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var prep = courses!.Courses.Single(course => course.Title == "CoursePrep");
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{prep.Id}/tree", TestContext.Current.CancellationToken);
        var lesson = tree!.Nodes.First(node => node.Type == "lesson");

        var first = await client.PostAsJsonAsync($"/api/v1/lessons/{lesson.Id}/preparations",
            new PreparationRequestModel("Compatibility"), TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync($"/api/v1/lessons/{lesson.Id}/preparations",
            new PreparationRequestModel("Compatibility"), TestContext.Current.CancellationToken);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var firstJob = await first.Content.ReadFromJsonAsync<PreparationJobModel>(cancellationToken: TestContext.Current.CancellationToken);
        var secondJob = await second.Content.ReadFromJsonAsync<PreparationJobModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(firstJob!.Id, secondJob!.Id);
    }

    [Fact]
    public async Task ManagedOutputsNeverAppearAsLessonsOrPlayableBeforeReady()
    {
        var client = application.CreateGuardedClient();

        // Let the worker's startup recovery and readiness checks settle before
        // driving the API, so the test observes the post-boot state.
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        if (!health.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"Ready check failed ({health.StatusCode}): {await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(4))}]");
        }

        var start = await client.StartScanAsync(TestContext.Current.CancellationToken);
        if (!start.IsSuccessStatusCode)
        {
            throw new Xunit.Sdk.XunitException(
                $"StartScan returned {start.StatusCode}: {await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)} Errors: [{string.Join(" | ", application.CapturedErrors.TakeLast(3))}]");
        }
        start.EnsureSuccessStatusCode();
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        await TestApplication.WaitForScanCompletionAsync(client, started!.ScanRunId);
        await WaitUntilSettledAsync(client);

        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var prep = courses!.Courses.Single(course => course.Title == "CoursePrep");
        Assert.Equal(3, prep.LessonCount);

        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{prep.Id}/tree", TestContext.Current.CancellationToken);
        Assert.Equal(3, tree!.Nodes.Count(node => node.Type == "lesson"));
    }
}