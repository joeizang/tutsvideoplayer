using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.IntegrationTests;

public class AppOptionsTests
{
    [Theory]
    [InlineData("~/LEARNING-VIDEOS")]
    [InlineData("LEARNING-VIDEOS")]
    public void ConfigurationResolvesLibraryAgainstHomeForBothBindingPaths(string configuredPath)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["App:LibraryRoot"] = configuredPath }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<AppOptions>().BindConfiguration(AppOptions.SectionName);
        using var provider = services.BuildServiceProvider();
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LEARNING-VIDEOS");

        Assert.Equal(expected, configuration.GetSection(AppOptions.SectionName).Get<AppOptions>()!.LibraryRoot);
        Assert.Equal(expected, provider.GetRequiredService<IOptions<AppOptions>>().Value.LibraryRoot);
    }

    [Fact]
    public void AbsoluteLibraryOverrideIsPreserved()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "custom-library");
        Assert.Equal(absolute, new AppOptions { LibraryRoot = absolute }.LibraryRoot);
    }
}
