using System.Net;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class HealthEndpointTests(TestApplication application)
{
    [Fact]
    public async Task LiveEndpointIsHealthy()
    {
        var client = application.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadyEndpointIsHealthyAndReportsSchemaState()
    {
        var client = application.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"status\":\"Healthy\"", body);
        Assert.Contains("database", body);
    }
}
