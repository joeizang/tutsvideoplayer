using TutsVideoPlayer.Infrastructure.FileSystem;

namespace TutsVideoPlayer.IntegrationTests;

[Collection(TestApplication.CollectionName)]
public class MediaFileLocatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;

    public MediaFileLocatorTests()
    {
        _root = Directory.CreateTempSubdirectory("tuts-media-root-").FullName;
        _outside = Directory.CreateTempSubdirectory("tuts-media-outside-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Fact]
    public void ResolvesPlainAndTrailingSeparatorRootSpellings()
    {
        var mediaPath = Path.Combine(_root, "video.mp4");
        File.WriteAllBytes(mediaPath, [1, 2, 3]);

        var plain = new MediaFileLocator(_root);
        var trailing = new MediaFileLocator(_root + Path.DirectorySeparatorChar);

        Assert.True(plain.TryResolve("video.mp4", out var plainPath));
        Assert.True(trailing.TryResolve("video.mp4", out var trailingPath));
        Assert.Equal(plainPath, trailingPath);
    }

    [Fact]
    public async Task RejectsSymlinkWhoseTargetIsOutsideTheRoot()
    {
        var outsideTarget = Path.Combine(_outside, "secret.txt");
        await File.WriteAllTextAsync(outsideTarget, "not media", TestContext.Current.CancellationToken);

        File.CreateSymbolicLink(Path.Combine(_root, "escape.mp4"), outsideTarget);

        var locator = new MediaFileLocator(_root);

        Assert.False(locator.TryResolve("escape.mp4", out var resolved));
        Assert.Equal(string.Empty, resolved);
        Assert.True(File.Exists(Path.Combine(_root, "escape.mp4")));
    }

    [Fact]
    public void ResolvesRegularFilesInsideTheRoot()
    {
        File.WriteAllBytes(Path.Combine(_root, "video.mp4"), [1, 2, 3]);

        var locator = new MediaFileLocator(_root);

        Assert.True(locator.TryResolve("video.mp4", out _));
    }

    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("sub/video.mp4")]
    [InlineData(".hidden.mp4")]
    [InlineData("notes.txt")]
    [InlineData("missing.mp4")]
    public void RejectsTraversalUnsupportedAndMissingNames(string relativeName)
    {
        var locator = new MediaFileLocator(_root);

        Assert.False(locator.TryResolve(relativeName, out _));
    }
}
