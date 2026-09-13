using System.Net;
using System.Net.Http.Headers;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class DemoMediaEndpointTests(TestApplication application)
{
    private const string FixtureUrl = "/media/demo/demo-fixture.mp4";

    [Fact]
    public async Task PlainGetReturnsFullBodyWithAcceptRanges()
    {
        var client = application.CreateClient();

        var response = await client.GetAsync(FixtureUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", response.Headers.AcceptRanges.First());
        Assert.True((response.Content.Headers.ContentLength ?? 0) > 0);
    }

    [Fact]
    public async Task RangeRequestReturnsPartialContent()
    {
        var client = application.CreateClient();

        var full = await client.GetAsync(FixtureUrl, TestContext.Current.CancellationToken);
        var total = full.Content.Headers.ContentLength
            ?? throw new InvalidOperationException("Fixture length was not reported.");

        var request = new HttpRequestMessage(HttpMethod.Get, FixtureUrl);
        request.Headers.Range = new RangeHeaderValue(0, 99);
        var partial = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal($"bytes 0-99/{total}", partial.Content.Headers.ContentRange?.ToString());
        Assert.Equal(100, partial.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task OutOfBoundsRangeReturnsRangeNotSatisfiable()
    {
        var client = application.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, FixtureUrl);
        request.Headers.Range = new RangeHeaderValue(999_999_999, null);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task HeadRequestReturnsHeadersWithoutBody()
    {
        var client = application.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, FixtureUrl), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bytes", response.Headers.AcceptRanges.First());
        Assert.True((response.Content.Headers.ContentLength ?? 0) > 0);
    }

    [Fact]
    public async Task UnknownFileNameReturnsProblemDetails()
    {
        var client = application.CreateClient();

        var response = await client.GetAsync("/media/demo/not-here.mp4", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/media/demo/..%2Fappsettings.json")]
    [InlineData("/media/demo/not-here.txt")]
    public async Task UnsafeOrUnsupportedNamesAreRejected(string url)
    {
        var client = application.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
