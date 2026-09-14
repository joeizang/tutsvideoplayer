using System.Net;
using System.Net.Http.Json;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// End-to-end course browsing: a course row has to open a lesson of that course, and the
/// course list must stay reachable beyond its first page.
/// </summary>
[Collection(TestApplication.CollectionName)]
public class CourseBrowsingTests(TestApplication application)
{
    [Fact]
    public async Task CourseRowsLinkThroughTheCourseEntryRoute()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        // A course ID used directly as a lesson ID is what opened another course's lesson;
        // the home page now links through the entry route that resolves a real lesson.
        var section = System.Text.RegularExpressions.Regex.Match(
            html, "<section[^>]*aria-labelledby=\"courses-heading\"[^>]*>.*?</section>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(section.Success, "The course-list section must be present.");
        Assert.Contains("href=\"/courses/", section.Value);
        Assert.DoesNotContain("href=\"/watch/", section.Value);
    }

    [Fact]
    public async Task CourseEntryRouteOpensALessonOfThatCourse()
    {
        var client = application.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        await application.EnsureCatalogScannedAsync();

        var courses = await client.GetFromJsonAsync<CourseListModel>(
            "/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var ts = courses!.Courses.Single(course => course.Title == "CourseTS");

        var response = await client.GetAsync($"/courses/{ts.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/watch/", location, StringComparison.Ordinal);

        var lessonId = location["/watch/".Length..];
        var lesson = await client.GetFromJsonAsync<LessonDetailModel>(
            $"/api/v1/lessons/{lessonId}", TestContext.Current.CancellationToken);
        Assert.Equal(ts.Id, lesson!.CourseId);
    }

    [Fact]
    public async Task HomePageOffersNavigationBeyondTheFirstPage()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var firstPage = await client.GetStringAsync("/?pageSize=2", TestContext.Current.CancellationToken);
        Assert.Contains("Page 1 of 2", firstPage);
        Assert.Contains("rel=\"next\"", firstPage);

        var secondPage = await client.GetStringAsync("/?page=2&pageSize=2", TestContext.Current.CancellationToken);
        Assert.Contains("Page 2 of 2", secondPage);
        Assert.Contains("rel=\"prev\"", secondPage);

        // Every fixture course has to be reachable across the two pages.
        Assert.Contains("CourseSubs", string.Concat(firstPage, secondPage));
        Assert.Contains("CourseAlpha", string.Concat(firstPage, secondPage));
        Assert.Contains("CourseTS", string.Concat(firstPage, secondPage));
    }

    [Fact]
    public async Task PaginationLinksCarryTheEffectivePageSize()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var firstPage = await client.GetStringAsync("/?pageSize=2", TestContext.Current.CancellationToken);

        // Following the generated link, rather than requesting page 2 directly: a Next link
        // without the page size lands on a page the controller sizes with its own default.
        var next = System.Text.RegularExpressions.Regex.Match(firstPage, "rel=\"next\"[^>]*href=\"([^\"]+)\"");
        if (!next.Success)
        {
            next = System.Text.RegularExpressions.Regex.Match(firstPage, "href=\"([^\"]+)\"[^>]*rel=\"next\"");
        }

        Assert.True(next.Success, "The first page must offer a Next link.");
        var href = System.Net.WebUtility.HtmlDecode(next.Groups[1].Value);
        Assert.Contains("pageSize=2", href, StringComparison.Ordinal);

        var secondPage = await client.GetStringAsync(href, TestContext.Current.CancellationToken);
        Assert.Contains("Page 2 of 2", secondPage);
    }

    [Fact]
    public async Task ReplayLinksCarryExplicitReplayIntent()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        // Drive one course to completion so the home page offers Replay course.
        var guarded = application.CreateClient();
        guarded.DefaultRequestHeaders.Add(TutsVideoPlayer.Web.Hosting.SameOriginMutationFilter.RequestHeaderName, "1");
        var courses = await guarded.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var subs = courses!.Courses.Single(course => course.Title == "CourseSubs");
        var tree = await guarded.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{subs.Id}/tree", TestContext.Current.CancellationToken);

        foreach (var lesson in tree!.Nodes.Where(node => node.Type == "lesson"))
        {
            var startResponse = await guarded.PostAsync($"/api/v1/lessons/{lesson.Id}/playback-sessions", null, TestContext.Current.CancellationToken);
            var session = await startResponse.Content.ReadFromJsonAsync<PlaybackSessionStartModel>(
                cancellationToken: TestContext.Current.CancellationToken);
            var write = await guarded.PutAsJsonAsync(
                $"/api/v1/playback-sessions/{session!.SessionId}/progress",
                new ProgressWriteModel(session.LastSequence + 1, 1, 1_000, IsPlaying: true, Ended: true),
                TestContext.Current.CancellationToken);
            write.EnsureSuccessStatusCode();
        }

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("Replay course", html);
        // Without this the replay opens at the saved position, a second from the end.
        Assert.Contains("?replay=1", html);
    }

    [Fact]
    public async Task HomeModelExposesTheActiveScanIdForResumedObservation()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        // The hydration payload has to include the scan the page can resume observing.
        Assert.Contains("\"scanRunId\"", html);
        Assert.Contains("\"latestScan\"", html);
    }
}
