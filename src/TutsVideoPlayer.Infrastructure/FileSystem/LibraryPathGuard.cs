using System.IO.Enumeration;
using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Infrastructure.FileSystem;

public static class LibraryPathGuard
{
    public static bool TryResolveWithin(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (!RelativeMediaPath.TryCreate(relativePath, out var path))
        {
            return false;
        }

        var trimmedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fileSystemRoot = Path.GetPathRoot(trimmedRoot);
        var prefix = string.Equals(fileSystemRoot, trimmedRoot, StringComparison.Ordinal)
            ? trimmedRoot
            : trimmedRoot + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(trimmedRoot, path.Value));
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
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
}