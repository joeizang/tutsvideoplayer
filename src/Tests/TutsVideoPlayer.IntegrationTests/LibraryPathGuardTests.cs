using TutsVideoPlayer.Infrastructure.FileSystem;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Media delivery containment. Prefix checks alone are not enough: a catalogued directory can
/// be replaced with a link to somewhere outside the library after the scan recorded it.
/// </summary>
public sealed class LibraryPathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tuts-guard-root-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("tuts-guard-outside-").FullName;

    public LibraryPathGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Course", "Section"));
        File.WriteAllBytes(Path.Combine(_root, "Course", "Section", "lesson.mp4"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(_outside, "secret.mp4"), new byte[64]);
        Directory.CreateDirectory(Path.Combine(_outside, "Section"));
        File.WriteAllBytes(Path.Combine(_outside, "Section", "lesson.mp4"), new byte[64]);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Fact]
    public void OrdinaryFileInsideTheRootResolves()
    {
        Assert.True(LibraryPathGuard.TryResolveWithin(_root, "Course/Section/lesson.mp4", out var resolved));
        Assert.StartsWith(_root, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void TraversalOutsideTheRootIsRejected()
    {
        Assert.False(LibraryPathGuard.TryResolveWithin(_root, "../escape.mp4", out _));
        Assert.False(LibraryPathGuard.TryResolveWithin(_root, "Course/../../escape.mp4", out _));
    }

    [Fact]
    public void SymlinkedTerminalFileIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_root, "Course", "linked.mp4");
        File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.mp4"));

        Assert.False(LibraryPathGuard.TryResolveWithin(_root, "Course/linked.mp4", out _));
    }

    [Fact]
    public void SymlinkedAncestorDirectoryIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The catalogued course directory is replaced with a link to an unrelated tree. The
        // terminal file then looks completely ordinary while its bytes come from outside.
        var course = Path.Combine(_root, "Swapped");
        Directory.CreateSymbolicLink(course, _outside);

        Assert.False(LibraryPathGuard.TryResolveWithin(_root, "Swapped/Section/lesson.mp4", out _));
        Assert.False(LibraryPathGuard.TryResolveWithin(_root, "Swapped/secret.mp4", out _));
    }

    [Fact]
    public void DeeplyNestedSymlinkedAncestorIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var swapped = Path.Combine(_root, "Course", "Linked");
        Directory.CreateSymbolicLink(swapped, Path.Combine(_outside, "Section"));

        Assert.False(LibraryPathGuard.TryResolveWithin(_root, "Course/Linked/lesson.mp4", out _));
    }

    [Fact]
    public void ALinkedRootIsStillUsable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Configuring the library through a link is legitimate; containment is then measured
        // against the real directory the link resolves to.
        var alias = Path.Combine(Path.GetTempPath(), $"tuts-guard-alias-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(alias, _root);
        try
        {
            Assert.True(LibraryPathGuard.TryResolveWithin(alias, "Course/Section/lesson.mp4", out var resolved));
            Assert.EndsWith(Path.Combine("Course", "Section", "lesson.mp4"), resolved, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }
}
