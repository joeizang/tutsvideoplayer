using System.Security.Cryptography;
using TutsVideoPlayer.Infrastructure.FileSystem;

namespace TutsVideoPlayer.Infrastructure.Catalog;

public static class PreparationArtifacts
{
    /// <summary>The managed quality cache lives here and nowhere else.</summary>
    public const string QualityAreaPrefix = ".tutsvideoplayer/quality/";

    /// <summary>
    /// True when a library-relative path addresses the managed quality area. Cache cleanup
    /// deletes files, so it is confined to the directory this application owns rather than
    /// trusting whatever path a catalog row happens to carry.
    /// </summary>
    public static bool IsInsideQualityArea(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith(QualityAreaPrefix, StringComparison.Ordinal);

    public static bool IsVerifiedOutput(string path, PreparationManifest manifest)
    {
        try
        {
            var file = new FileInfo(path);
            return manifest.State is "Prepared" or "Committed"
                && manifest.OutputFileName == file.Name && file.Exists && file.LinkTarget is null
                && manifest.OutputLengthBytes == file.Length && manifest.OutputHash.Length == 64
                && Hash(path).Equals(manifest.OutputHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsOwnedOutput(string root, string relativePath)
    {
        if (relativePath.EndsWith(".tvp.json", StringComparison.OrdinalIgnoreCase))
            return IsOwnedOutput(root, relativePath[..^9]);
        if (!relativePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || !LibraryPathGuard.TryResolveWithin(root, relativePath, out var output)
            || !LibraryPathGuard.TryResolveWithin(root, relativePath + ".tvp.json", out var sidecar)) return false;
        var manifest = PreparationManifest.TryRead(sidecar);
        return manifest is not null && manifest.Sources.Count > 0 && !string.IsNullOrEmpty(manifest.RecipeVersion)
            && manifest.State is "Prepared" or "Committed"
            && manifest.OutputFileName == Path.GetFileName(output)
            && manifest.OutputHash.Length == 64
            && manifest.OutputLengthBytes == new FileInfo(output).Length;
    }

    public static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static string? FindContainingMount(string directory, IEnumerable<string> mounts)
    {
        var path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return mounts.OrderByDescending(mount => mount.Length).FirstOrDefault(mount =>
            path.StartsWith(mount.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
