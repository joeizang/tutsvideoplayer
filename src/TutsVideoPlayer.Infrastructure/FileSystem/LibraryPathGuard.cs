using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Infrastructure.FileSystem;

public static class LibraryPathGuard
{
    /// <summary>
    /// Resolves a library-relative path to an absolute path that is provably inside the
    /// configured root.
    /// </summary>
    /// <remarks>
    /// Prefix containment alone is not enough. A catalogued directory can be replaced with a
    /// symbolic link to somewhere outside the library after it was scanned; the terminal file
    /// then looks perfectly ordinary while the bytes come from outside the root. Every segment
    /// between the root and the file is therefore checked for a reparse point, which is the
    /// same rule the library enumerator applies when it refuses to descend into links.
    /// </remarks>
    public static bool TryResolveWithin(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(root) || !RelativeMediaPath.TryCreate(relativePath, out var path))
        {
            return false;
        }

        // The configured root itself may legitimately be a link (a mounted library, a
        // convenience alias). It is resolved to its real location once, so containment is
        // measured against real directories rather than the link's own spelling.
        var trimmedRoot = ResolveRoot(root);
        if (trimmedRoot is null)
        {
            return false;
        }

        var fileSystemRoot = Path.GetPathRoot(trimmedRoot);
        var prefix = string.Equals(fileSystemRoot, trimmedRoot, StringComparison.Ordinal)
            ? trimmedRoot
            : trimmedRoot + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(trimmedRoot, path.Value));
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (ContainsLinkBelowRoot(trimmedRoot, candidate))
        {
            return false;
        }

        var info = new FileInfo(candidate);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    public static string? ResolveRoot(string root)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        try
        {
            var resolved = Directory.ResolveLinkTarget(fullRoot, returnFinalTarget: true);
            return resolved is null
                ? fullRoot
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true when any directory strictly between the root and the candidate file is a
    /// reparse point (symbolic link, junction or mount point).
    /// </summary>
    private static bool ContainsLinkBelowRoot(string root, string candidate)
    {
        var directory = Path.GetDirectoryName(candidate);

        while (directory is not null
            && !string.Equals(directory, root, StringComparison.Ordinal)
            && directory.StartsWith(root, StringComparison.Ordinal))
        {
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(directory);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        // A path that walked past the root instead of reaching it is not contained at all.
        return directory is null || !string.Equals(directory, root, StringComparison.Ordinal);
    }
}
