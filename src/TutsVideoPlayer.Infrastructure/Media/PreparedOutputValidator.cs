using System.Security.Cryptography;

namespace TutsVideoPlayer.Infrastructure.Media;

public sealed record OutputValidation(
    bool Success,
    string? Error,
    ProbeMetadata Probe,
    long ByteLength,
    string OutputHash,
    bool IsContentFailure = false)
{
    public static OutputValidation Fail(string error, bool isContentFailure = false) =>
        new(false, error, new ProbeMetadata(null, null, null, null, null), 0, string.Empty, isContentFailure);
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
        CancellationToken cancellationToken = default,
        bool requireConstantFrameRate = false)
    {
        var file = new FileInfo(outputPath);
        if (!file.Exists)
        {
            return OutputValidation.Fail("The prepared output is missing or unavailable.");
        }
        if (file.Length == 0) return OutputValidation.Fail("The prepared output is empty.", isContentFailure: true);

        var probe = await probeAdapter.ProbeAsync(outputPath, cancellationToken);
        if (!probe.Success)
        {
            return OutputValidation.Fail(probe.Error ?? "The prepared output could not be probed.");
        }

        if (probe.VideoCodec != (expectedVideoCodec ?? "h264"))
        {
            return OutputValidation.Fail(
                $"The prepared output video codec is {probe.VideoCodec ?? "unknown"}, expected {expectedVideoCodec ?? "h264"}.", isContentFailure: true);
        }

        if (requireConstantFrameRate && probe.IsVariableFrameRate == true)
        {
            return OutputValidation.Fail("The prepared output still has irregular video frame timing.", isContentFailure: true);
        }

        if (expectedAudioCodec is not null && probe.AudioCodec != expectedAudioCodec)
        {
            return OutputValidation.Fail(
                $"The prepared output audio codec is {probe.AudioCodec ?? "missing"}, expected {expectedAudioCodec}.", isContentFailure: true);
        }

        if (sourceDurationMs is > 0 && probe.DurationMs is > 0)
        {
            var tolerance = Math.Max(2_000, (long)(sourceDurationMs.Value * 0.01));
            if (Math.Abs(probe.DurationMs.Value - sourceDurationMs.Value) > tolerance)
            {
                return OutputValidation.Fail(
                    $"The prepared output duration {probe.DurationMs} ms differs from the source {sourceDurationMs} ms by more than {tolerance} ms.", isContentFailure: true);
            }
        }

        var hash = await ComputeHashAsync(outputPath, cancellationToken);
        return new OutputValidation(
            true,
            null,
            probe.ToMetadata(),
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
