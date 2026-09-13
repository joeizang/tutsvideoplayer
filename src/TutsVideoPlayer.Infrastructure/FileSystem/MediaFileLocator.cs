namespace TutsVideoPlayer.Infrastructure.FileSystem;

public sealed class MediaFileLocator
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".vtt"
    };

    private readonly string _root;

    public MediaFileLocator(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
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
        if (!candidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        if (!File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
