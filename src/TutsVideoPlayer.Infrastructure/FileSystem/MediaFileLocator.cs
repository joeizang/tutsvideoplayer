namespace TutsVideoPlayer.Infrastructure.FileSystem;

public sealed class MediaFileLocator
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".vtt"
    };

    private readonly string _root;
    private readonly string _containmentPrefix;

    public MediaFileLocator(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fileSystemRoot = Path.GetPathRoot(_root);
        _containmentPrefix = string.Equals(fileSystemRoot, _root, StringComparison.Ordinal)
            ? _root
            : _root + Path.DirectorySeparatorChar;
    }

    public bool TryResolve(string relativeName, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativeName))
        {
            return false;
        }

        if (relativeName.Contains('/') || relativeName.Contains('\\') || relativeName.StartsWith('.'))
        {
            return false;
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(relativeName)))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, relativeName));
        if (!candidate.StartsWith(_containmentPrefix, StringComparison.Ordinal))
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
