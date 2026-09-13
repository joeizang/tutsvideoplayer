using System.Security.Cryptography;

namespace TutsVideoPlayer.Infrastructure.Catalog;

/// <summary>
/// Calculates complete SHA-256 content digests for source components.
/// Size and modification time are inexpensive change hints; only a digest is
/// evidence that the bytes behind an existing relative path are the same bytes.
/// </summary>
public static class ContentFingerprint
{
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Returns the lowercase hexadecimal SHA-256 digest of the file, or <c>null</c>
    /// when the content could not be read. An unknown fingerprint stays explicitly
    /// unknown rather than being represented as the digest of an empty value.
    /// </summary>
    public static async Task<string?> ComputeAsync(string absolutePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                absolutePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

            var digest = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexStringLower(digest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
