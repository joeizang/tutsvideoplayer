using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class LibraryEndpointTests(TestApplication application)
{
    private static async Task<ScanStatusModel> WaitForScanCompletionAsync(HttpClient client, string scanRunId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var scan = await client.GetFromJsonAsync<ScanStatusModel>($"/api/v1/library/scans/{scanRunId}", TestContext.Current.CancellationToken);
            if (scan is not null && scan.State != "Running")
            {
                return scan;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The scan did not finish in time.");
    }

    [Fact]
    public async Task ScanPersistsCatalogAndLibrarySummaryReflectsIt()
    {
        var client = application.CreateClient();

        var start = await client.StartScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(started);

        var finished = await WaitForScanCompletionAsync(client, started.ScanRunId);
        Assert.Equal("Succeeded", finished.State);

        var summary = await client.GetFromJsonAsync<LibrarySummaryModel>("/api/v1/library", TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(3, summary.CourseCount);
        Assert.Equal(7, summary.AvailableLessonCount);
        Assert.Equal(0, summary.MissingLessonCount);
        Assert.Equal(3, summary.SubtitleTrackCount);
        Assert.True(summary.CatalogRevision > 0);
        Assert.NotNull(summary.LatestScan);
        Assert.Equal("Succeeded", summary.LatestScan.State);
        Assert.True(summary.Status.RootAvailable);
        Assert.True(summary.Status.SchemaReady);
    }

    [Fact]
    public async Task RepeatedScanRequestsJoinTheActiveScan()
    {
        var client = application.CreateClient();

        var first = await client.StartScanAsync(TestContext.Current.CancellationToken);
        var second = await client.StartScanAsync(TestContext.Current.CancellationToken);

        var firstResult = await first.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        var secondResult = await second.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.Equal(firstResult.ScanRunId, secondResult.ScanRunId);
        Assert.True(secondResult.Joined);

        await WaitForScanCompletionAsync(client, secondResult.ScanRunId);
    }

    [Fact]
    public async Task CourseSearchMatchesCourseAndLessonNames()
    {
        var client = application.CreateClient();

        var response = await client.GetStringAsync("/api/v1/courses?q=basics", TestContext.Current.CancellationToken);
        var model = JsonSerializer.Deserialize<CourseListModel>(response, JsonOptions());
        Assert.NotNull(model);
        Assert.Contains(model.Courses, course => course.Title == "CourseAlpha");

        var miss = await client.GetStringAsync("/api/v1/courses?q=%25zzzznope%25", TestContext.Current.CancellationToken);
        Assert.Contains("\"totalCount\":0", miss);
    }

    [Fact]
    public async Task CourseTreeReturnsNaturallyOrderedFlatNodes()
    {
        var client = application.CreateClient();
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses", TestContext.Current.CancellationToken);
        var alpha = courses!.Courses.Single(course => course.Title == "CourseAlpha");

        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{alpha.Id}/tree", TestContext.Current.CancellationToken);
        Assert.NotNull(tree);

        var lessonNodes = tree.Nodes.Where(node => node.Type == "lesson").ToList();
        Assert.Equal(
            ["CourseAlpha/01 Intro/02 Getting Started.mp4", "CourseAlpha/03 Setup/02 Basics.mp4", "CourseAlpha/03 Setup/10 Advanced.mp4"],
            lessonNodes.Select(node => node.Filename));

        Assert.Equal(2, tree.Nodes.Count(node => node.Type == "folder"));
    }

    [Fact]
    public async Task LessonDetailsIncludeNeighboursAndProbe()
    {
        var client = application.CreateClient();
        var tree = await client.GetFromJsonAsync<CourseTreeModel>("/api/v1/courses/1/tree", TestContext.Current.CancellationToken);
        var firstLesson = tree!.Nodes.First(node => node.Type == "lesson");

        var lesson = await client.GetFromJsonAsync<LessonDetailModel>($"/api/v1/lessons/{firstLesson.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(lesson);
        Assert.Equal("CourseAlpha/01 Intro/02 Getting Started.mp4", lesson.Filename);
        Assert.Equal("Available", lesson.Availability);
        Assert.Null(lesson.PreviousLessonId);
        Assert.NotNull(lesson.NextLessonId);
        Assert.Null(lesson.DurationMs);
        Assert.Equal(0, lesson.SubtitleCandidateCount);
    }

    [Fact]
    public async Task UnknownCourseAndLessonReturnProblemDetails()
    {
        var client = application.CreateClient();

        var course = await client.GetAsync("/api/v1/courses/99999/tree", TestContext.Current.CancellationToken);
        var lesson = await client.GetAsync("/api/v1/lessons/99999", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, course.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lesson.StatusCode);
        Assert.Equal("application/problem+json", course.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ScanIssuesEndpointReturnsPagedIssues()
    {
        var client = application.CreateClient();

        var response = await client.GetStringAsync("/api/v1/library/scans/1/issues?page=1&pageSize=5", TestContext.Current.CancellationToken);
        Assert.Contains("\"code\"", response);
    }

    [Fact]
    public async Task CourseListKeepsBoundedCommandCount()
    {
        var client = application.CreateClient();
        application.CommandCapture.Reset();

        _ = await client.GetStringAsync("/api/v1/courses?page=1&pageSize=10", TestContext.Current.CancellationToken);

        Assert.True(application.CommandCapture.CommandCount <= 2, $"Course list used {application.CommandCapture.CommandCount} commands.");
    }

    [Fact]
    public async Task CourseTreeKeepsBoundedCommandCount()
    {
        var client = application.CreateClient();
        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var alpha = courses!.Courses.Single(course => course.Title == "CourseAlpha");

        application.CommandCapture.Reset();
        _ = await client.GetStringAsync($"/api/v1/courses/{alpha.Id}/tree", TestContext.Current.CancellationToken);

        Assert.True(application.CommandCapture.CommandCount <= 3, $"Tree used {application.CommandCapture.CommandCount} commands.");
    }

    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);
}