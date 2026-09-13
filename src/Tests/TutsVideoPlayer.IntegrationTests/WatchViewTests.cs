using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class WatchViewTests(TestApplication application)
{
    [Fact]
    public async Task WatchPageRendersLessonRailFromRealCatalog()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var alpha = courses!.Courses.Single(course => course.Title == "CourseAlpha");
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{alpha.Id}/tree", TestContext.Current.CancellationToken);
        var firstLesson = tree!.Nodes.First(node => node.Type == "lesson");

        var response = await client.GetAsync($"/watch/{firstLesson.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Getting Started", html);
        Assert.Contains("CourseAlpha", html);
        Assert.Contains("lesson-rail", html);
        Assert.Contains("Playback arrives in milestone 2", html);
        Assert.Contains("02 Getting Started.mp4", html);
        Assert.Contains("/watch/", html);
    }
}
