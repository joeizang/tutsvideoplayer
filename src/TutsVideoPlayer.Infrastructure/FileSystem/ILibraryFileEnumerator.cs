namespace TutsVideoPlayer.Infrastructure.FileSystem;

public sealed record LibraryFileEntry(string RelativePath, long LengthBytes, long ModifiedUtcMs);

public sealed record LibraryEnumerationIssue(string RelativeDirectory, string Code, string Message);

public sealed class LibraryEnumeration
{
    public List<LibraryFileEntry> Files { get; } = [];
    public List<LibraryEnumerationIssue> Issues { get; } = [];
    public HashSet<string> EnumeratedDirectories { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailedDirectories { get; } = new(StringComparer.Ordinal);
}

public interface ILibraryFileEnumerator
{
    LibraryEnumeration Enumerate(string root, CancellationToken cancellationToken = default);
}
