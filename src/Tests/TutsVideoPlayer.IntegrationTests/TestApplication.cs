using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutsVideoPlayer.IntegrationTests;

[CollectionDefinition(TestApplication.CollectionName)]
public sealed class WebApplicationCollection : ICollectionFixture<TestApplication>
{
}

public sealed class TestApplication : WebApplicationFactory<Program>
{
    public const string CollectionName = "TutsVideoPlayerWebApplication";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
    }
}
