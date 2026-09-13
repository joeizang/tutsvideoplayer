using System.Net;
using System.Net.Http.Json;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class SystemEndpointTests(TestApplication application)
{
    [Fact]
    public async Task SystemInfoReturnsCamelCaseJson()
    {
        var client = application.CreateClient();

        var response = await client.GetAsync("/api/v1/system/info", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var model = await response.Content.ReadFromJsonAsync<SystemInfoModel>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(model);
        Assert.Equal("Tuts Video Player", model.ApplicationName);
        Assert.Equal("/media/demo/demo-fixture.mp4", model.FixtureMediaUrl);
    }

    [Fact]
    public async Task UnknownSystemRouteReturnsProblemDetails()
    {
        var client = application.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/unknown");
        request.Headers.Accept.ParseAdd("application/json");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("traceId", body);
    }
}
