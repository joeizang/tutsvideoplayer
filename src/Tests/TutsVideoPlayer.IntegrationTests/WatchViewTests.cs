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
        Assert.Contains("Course lessons", html);
        Assert.Contains("no ready playable rendition", html);
        Assert.Contains("02 Getting Started.mp4", html);
        Assert.Contains("/watch/", html);
    }

    [Fact]
    public async Task LessonRailOpensOnlyTheModuleHoldingTheCurrentLesson()
    {
        var client = application.CreateClient();
        await application.EnsureCatalogScannedAsync();

        var courses = await client.GetFromJsonAsync<CourseListModel>("/api/v1/courses?pageSize=100", TestContext.Current.CancellationToken);
        var alpha = courses!.Courses.Single(course => course.Title == "CourseAlpha");
        var tree = await client.GetFromJsonAsync<CourseTreeModel>($"/api/v1/courses/{alpha.Id}/tree", TestContext.Current.CancellationToken);
        var intro = tree!.Nodes.Single(node => node.Type == "folder" && node.Title.Contains("Intro"));
        var setup = tree.Nodes.Single(node => node.Type == "folder" && node.Title.Contains("Setup"));
        var introLesson = tree.Nodes.Single(node => node.Type == "lesson" && node.FolderId == intro.Id);
        var setupLessons = tree.Nodes.Where(node => node.Type == "lesson" && node.FolderId == setup.Id).ToList();

        var response = await client.GetAsync($"/watch/{introLesson.Id}", TestContext.Current.CancellationToken);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // Scoped to the rail: the Next link below the player legitimately points into the
        // collapsed module, so a whole-page search would not show whether the rail is closed.
        var railStart = html.IndexOf("aria-label=\"Course lessons\"", StringComparison.Ordinal);
        Assert.True(railStart >= 0);
        var rail = html[railStart..html.IndexOf("</nav>", railStart, StringComparison.Ordinal)];

        Assert.Contains(setup.Title, rail);
        Assert.Contains($"/watch/{introLesson.Id}", rail);
        foreach (var lesson in setupLessons)
        {
            Assert.DoesNotContain($"/watch/{lesson.Id}", rail);
        }
    }
}
