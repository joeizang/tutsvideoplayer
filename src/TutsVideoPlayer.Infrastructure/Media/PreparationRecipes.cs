using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Infrastructure.Media;

public sealed record PreparationRecipe(
    string RecipeVersion,
    string OutputExtension,
    IReadOnlyList<string> Arguments,
    string? ExpectedAudioCodec,
    bool RequiresCompanionAudio,
    bool RequiresConstantFrameRate = false);

public sealed record SourceSetInfo(
    string PrimaryRelativePath,
    string PrimaryFullPath,
    string? CompanionRelativePath,
    string? CompanionFullPath,
    ProbeMetadata? Probe);

public static class PreparationRecipes
{
    public const string WmvVersion = "wmv-h264-v2";
    public const string CompatibilityVersion = "compat-h264-v2";
    public const string NormalizationVersion = "cfr-h264-v1";
    public const string TsAacVersion = "tsaac-mux-v1";
    public const string RemuxVersion = "remux-v1";

    /// <summary>
    /// Chooses the permanent compatibility recipe for a source set, or returns a
    /// reason when the source cannot be honestly prepared in this version.
    /// </summary>
    public static PreparationRecipe? Choose(SourceSetInfo source, out string? blockedReason)
    {
        blockedReason = null;
        var extension = Path.GetExtension(source.PrimaryRelativePath).ToLowerInvariant();

        // Normalization must precede stream-copy recipes, including H.264/AAC
        // MP4 and split TS sources whose codecs are already browser-compatible.
        if (source.Probe?.IsVariableFrameRate == true)
        {
            var arguments = new List<string> { "-i", source.PrimaryFullPath };
            arguments.AddRange(source.CompanionFullPath is not null
                ? ["-i", source.CompanionFullPath, "-map", "0:v:0", "-map", "1:a:0"]
                : ["-map", "0:v:0", "-map", "0:a:0?"]);
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2"]);
            arguments.AddRange(FrameRatePolicy.EncoderArguments(source.Probe));
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4", "-y", "{output}"]);
            return new PreparationRecipe(NormalizationVersion, ".mp4", arguments,
                source.CompanionFullPath is not null || source.Probe.AudioCodec is not null ? "aac" : null, false, true);
        }

        if (extension is ".wmv")
        {
            var arguments = new List<string>
            {
                "-i", source.PrimaryFullPath
            };
            if (source.CompanionFullPath is not null)
            {
                arguments.AddRange(["-i", source.CompanionFullPath, "-map", "0:v:0", "-map", "1:a:0"]);
            }
            else
            {
                arguments.AddRange(["-map", "0:v:0", "-map", "0:a:0?"]);
            }

            arguments.AddRange(
            [
                "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k",
                .. FrameRatePolicy.EncoderArguments(source.Probe),
                "-movflags", "+faststart",
                "-f", "mp4", "-y", "{output}"
            ]);
            return new PreparationRecipe(WmvVersion, ".mp4", arguments, source.CompanionFullPath is not null || source.Probe?.AudioCodec is not null ? "aac" : null, RequiresCompanionAudio: false, RequiresConstantFrameRate: true);
        }

        if (extension is ".ts" or ".mts" or ".m2ts")
        {
            if (source.CompanionRelativePath is not null)
            {
                if (source.Probe?.VideoCodec != "h264")
                {
                    blockedReason = "unsupported-source-codecs";
                    return null;
                }
                // Split-stream course material: map the video and its companion audio
                // explicitly, stream-copy both, and let validation prove the result.
                var arguments = new List<string>
                {
                    "-fflags", "+genpts",
                    "-i", source.PrimaryFullPath,
                    "-i", source.CompanionFullPath!,
                    "-map", "0:v:0",
                    "-map", "1:a:0",
                    "-c", "copy",
                    "-movflags", "+faststart",
                    "-f", "mp4", "-y", "{output}"
                };
                return new PreparationRecipe(TsAacVersion, ".mp4", arguments, "aac", RequiresCompanionAudio: true);
            }

            var probe = source.Probe;
            if (probe?.VideoCodec is "h264" && probe.AudioCodec is "aac")
            {
                var remux = new List<string>
                {
                    "-fflags", "+genpts",
                    "-i", source.PrimaryFullPath,
                    "-map", "0:v:0",
                    "-map", "0:a:0",
                    "-c", "copy",
                    "-movflags", "+faststart",
                    "-f", "mp4", "-y", "{output}"
                };
                return new PreparationRecipe(RemuxVersion, ".mp4", remux, "aac", RequiresCompanionAudio: false);
            }

            blockedReason = "unsupported-source-codecs";
            return null;
        }

        if (probeCodecsAreCopyable(source.Probe))
        {
            var remux = new List<string>
            {
                "-i", source.PrimaryFullPath,
                "-map", "0:v:0",
                "-map", "0:a:0?",
                "-c", "copy",
                "-movflags", "+faststart",
                "-f", "mp4", "-y", "{output}"
            };
            return new PreparationRecipe(RemuxVersion, ".mp4", remux, source.Probe?.AudioCodec, RequiresCompanionAudio: false);
        }

        // Explicit browser fallback and non-exempt containers use a conservative SDR recipe.
        if (extension is ".mp4" or ".mkv" or ".m4v" or ".webm"
            && source.Probe?.VideoCodec is "h264" or "vp8" or "vp9" or "mpeg4")
        {
            return new PreparationRecipe(CompatibilityVersion, ".mp4",
                ["-i", source.PrimaryFullPath, "-map", "0:v:0", "-map", "0:a:0?",
                 "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                 "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "192k",
                 .. FrameRatePolicy.EncoderArguments(source.Probe),
                 "-movflags", "+faststart", "-f", "mp4", "-y", "{output}"],
                source.Probe.AudioCodec is null ? null : "aac", false, true);
        }

        blockedReason = "unsupported-source-container";
        return null;
    }

    private static bool probeCodecsAreCopyable(ProbeMetadata? probe) =>
        probe?.VideoCodec is "h264" && (probe.AudioCodec is "aac" or "mp3" or null);
}
