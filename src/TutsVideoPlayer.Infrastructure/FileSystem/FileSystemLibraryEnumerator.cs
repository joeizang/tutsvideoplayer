namespace TutsVideoPlayer.Infrastructure.FileSystem;

public sealed class FileSystemLibraryEnumerator : ILibraryFileEnumerator
{
    public LibraryEnumeration Enumerate(string root, CancellationToken cancellationToken = default)
    {
        var result = new LibraryEnumeration();
        var rootDirectory = new DirectoryInfo(root);

        if (!rootDirectory.Exists)
        {
            result.FailedDirectories.Add(string.Empty);
            result.Issues.Add(new LibraryEnumerationIssue(
                string.Empty,
                "RootUnavailable",
                "The configured library root does not exist or is not a directory."));
            return result;
        }

        Walk(rootDirectory, string.Empty, result, cancellationToken);
        return result;
    }

    private static void Walk(DirectoryInfo directory, string relativePrefix, LibraryEnumeration result, CancellationToken cancellationToken)
    {
        result.EnumeratedDirectories.Add(relativePrefix);

        FileSystemInfo[] children;
        try
        {
            children = directory.EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            result.FailedDirectories.Add(relativePrefix);
            result.Issues.Add(new LibraryEnumerationIssue(
                relativePrefix,
                "DirectoryEnumerationFailed",
                $"The directory could not be read: {exception.GetType().Name}."));
            return;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var childRelative = relativePrefix.Length == 0
                ? child.Name
                : $"{relativePrefix}/{child.Name}";

            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.Issues.Add(new LibraryEnumerationIssue(
                    childRelative,
                    "SymlinkExcluded",
                    "Symbolic links are excluded from the library in this version."));
                continue;
            }

            if (child is DirectoryInfo subDirectory)
            {
                Walk(subDirectory, childRelative, result, cancellationToken);
                continue;
            }

            result.Files.Add(new LibraryFileEntry(
                childRelative,
                Math.Max(0, ((FileInfo)child).Length),
                new DateTimeOffset(((FileInfo)child).LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
    }
}
