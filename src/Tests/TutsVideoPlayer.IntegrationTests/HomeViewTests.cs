using System.Net;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class HomeViewTests(TestApplication application)
{
    [Fact]
    public async Task HomeReturnsServerRenderedCourseListWithHydrationPayload()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<h1>Tutorial library</h1>", html);
        Assert.Contains("Courses</h2>", html);
        Assert.Contains("CourseAlpha", html);
        Assert.Contains("CourseTS", html);
        Assert.Contains("application/json", html);
        Assert.Contains("jsxcore-model", html);
        Assert.Contains("mountView", html);
        Assert.Contains("<title>Tutorial library</title>", html);
    }

    [Fact]
    public async Task HomeHtmlContainsNoExternalAssetRequests()
    {
        var client = application.CreateClient();

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("http://cdn", html);
        Assert.DoesNotContain("https://unpkg.com", html);
        Assert.DoesNotContain("https://cdn.jsdelivr.net", html);
        Assert.DoesNotContain("fonts.googleapis.com", html);
    }
}