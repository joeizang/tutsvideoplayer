using System.Net;
using System.Net.Http.Json;
using System.Text;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Browser and local-network protection: a cross-site page must not be able to trigger
/// filesystem scanning and probing, even though the API has no login.
/// </summary>
[Collection(TestApplication.CollectionName)]
public class ScanRequestProtectionTests(TestApplication application)
{
    private static HttpRequestMessage ScanRequest() => new(HttpMethod.Post, "/api/v1/library/scans");

    [Fact]
    public async Task CrossSiteFormPostIsRejected()
    {
        var client = application.CreateClient();
        var request = ScanRequest();
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Content = new StringContent("scan=1", Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CrossSiteFetchMetadataIsRejected()
    {
        var client = application.CreateClient();
        var request = ScanRequest();
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add(SameOriginMutationFilter.RequestHeaderName, "1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnrecognizedOriginIsRejected()
    {
        var client = application.CreateClient();
        var request = ScanRequest();
        request.Headers.Add("Origin", "http://evil.example");
        request.Headers.Add(SameOriginMutationFilter.RequestHeaderName, "1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingSameOriginHeaderIsRejected()
    {
        var client = application.CreateClient();

        var response = await client.SendAsync(ScanRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SameOriginJsonRequestIsAccepted()
    {
        var client = application.CreateClient();

        var response = await client.StartScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = await response.Content.ReadFromJsonAsync<ScanStartResultModel>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(started);
    }

    [Fact]
    public async Task ReadRequestsAreUnaffected()
    {
        var client = application.CreateClient();

        var response = await client.GetAsync("/api/v1/library", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
