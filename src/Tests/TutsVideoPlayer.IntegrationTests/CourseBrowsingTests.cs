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
        Assert.Contains("href=\"/courses/", html);
        Assert.DoesNotContain("href=\"/watch/", html);
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
