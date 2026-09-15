using System.Security.Cryptography;

namespace TutsVideoPlayer.Infrastructure.Media;

public sealed record OutputValidation(
    bool Success,
    string? Error,
    ProbeMetadata Probe,
    long ByteLength,
    string OutputHash)
{
    public static OutputValidation Fail(string error) =>
        new(false, error, new ProbeMetadata(null, null, null, null, null), 0, string.Empty);
}

/// <summary>
/// Validates a prepared output before it can be published: successful probe,
/// expected codecs, duration within max(2 s, 1 %) of the source, and a content
/// digest recorded for recovery.
/// </summary>
public sealed class PreparedOutputValidator(FFprobeAdapter probeAdapter)
{
    public async Task<OutputValidation> ValidateAsync(
        string outputPath,
        long? sourceDurationMs,
        string? expectedVideoCodec,
        string? expectedAudioCodec,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(outputPath);
        if (!file.Exists || file.Length == 0)
        {
            return OutputValidation.Fail("The prepared output is empty or missing.");
        }

        var probe = await probeAdapter.ProbeAsync(outputPath, cancellationToken);
        if (!probe.Success)
        {
            return OutputValidation.Fail(probe.Error ?? "The prepared output could not be probed.");
        }

        if (probe.VideoCodec != (expectedVideoCodec ?? "h264"))
        {
            return OutputValidation.Fail(
                $"The prepared output video codec is {probe.VideoCodec ?? "unknown"}, expected {expectedVideoCodec ?? "h264"}.");
        }

        if (expectedAudioCodec is not null && probe.AudioCodec != expectedAudioCodec)
        {
            return OutputValidation.Fail(
                $"The prepared output audio codec is {probe.AudioCodec ?? "missing"}, expected {expectedAudioCodec}.");
        }

        if (sourceDurationMs is > 0 && probe.DurationMs is > 0)
        {
            var tolerance = Math.Max(2_000, (long)(sourceDurationMs.Value * 0.01));
            if (Math.Abs(probe.DurationMs.Value - sourceDurationMs.Value) > tolerance)
            {
                return OutputValidation.Fail(
                    $"The prepared output duration {probe.DurationMs} ms differs from the source {sourceDurationMs} ms by more than {tolerance} ms.");
            }
        }

        var hash = await ComputeHashAsync(outputPath, cancellationToken);
        return new OutputValidation(
            true,
            null,
            new ProbeMetadata(probe.DurationMs, probe.VideoCodec, probe.AudioCodec, probe.Width, probe.Height),
            file.Length,
            hash);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}