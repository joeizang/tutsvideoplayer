using System.Security.Cryptography;
using System.Text;

namespace TutsVideoPlayer.Infrastructure.Media;

public static class SourceFingerprint
{
    /// <summary>
    /// Full source-set digest: complete content of the video and every required
    /// companion component plus their roles and library-relative paths. A changed
    /// companion audio invalidates the set exactly like a changed video.
    /// </summary>
    public static string Compute(IReadOnlyList<(string Role, string RelativePath, string AbsolutePath)> components)
    {
        if (components.Count == 0)
        {
            throw new ArgumentException("A source set needs at least one component.", nameof(components));
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var component in components.OrderBy(entry => entry.Role, StringComparer.Ordinal)
                     .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{component.Role}|{component.RelativePath}|"));

            using var stream = File.OpenRead(component.AbsolutePath);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}