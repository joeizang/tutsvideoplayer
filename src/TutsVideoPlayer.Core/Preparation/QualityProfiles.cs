using System.Globalization;

namespace TutsVideoPlayer.Core.Preparation;

/// <summary>
/// Quality profiles: native stays with the source rendition; 1080/720/480 are
/// height targets. Eligibility never upscales — a profile whose target height
/// is not strictly below the source display height is not offered, and a
/// source whose native height already equals a profile is served by its native
/// rendition instead of a duplicate.
/// </summary>
public static class QualityProfiles
{
    public const string Native = "Native";
    public const string P1080 = "1080";
    public const string P720 = "720";
    public const string P480 = "480";

    public static readonly IReadOnlyList<string> All = [P1080, P720, P480];

    public static bool IsKnown(string? profile) =>
        profile is not null && All.Any(candidate => candidate.Equals(profile, StringComparison.OrdinalIgnoreCase));

    public static int? TargetHeight(string? profile) => profile?.ToLowerInvariant() switch
    {
        P1080 => 1080,
        P720 => 720,
        P480 => 480,
        _ => null
    };

    /// <summary>Profiles the source is actually eligible for, in descending target order.</summary>
    public static IReadOnlyList<string> EligibleProfiles(int? sourceWidth, int? sourceHeight)
    {
        if (sourceHeight is not > 0)
        {
            return [];
        }

        return All.Where(profile => TargetHeight(profile)!.Value < sourceHeight.Value).ToList();
    }

    /// <summary>
    /// Display dimensions scaled to the target height with the aspect preserved,
    /// rounded to even numbers as H.264 requires. Never upscales.
    /// </summary>
    public static (int Width, int Height) ScaledDimensions(int sourceWidth, int sourceHeight, int targetHeight)
    {
        if (targetHeight >= sourceHeight)
        {
            throw new ArgumentException("A quality profile may never upscale the source.");
        }

        var height = targetHeight;
        var width = (int)Math.Round(sourceWidth * (double)targetHeight / sourceHeight, MidpointRounding.AwayFromZero);
        if (width % 2 != 0)
        {
            width--;
        }

        if (height % 2 != 0)
        {
            height--;
        }

        return (Math.Max(2, width), Math.Max(2, height));
    }

    public static string RecipeVersionFor(string profile) =>
        $"quality-{profile.ToLowerInvariant()}-v1";

    /// <summary>An honest, dimension-aware label for a rendition.</summary>
    public static string LabelFor(string? profile, int? width, int? height)
    {
        if (profile is null || TargetHeight(profile) is null)
        {
            return width is not null && height is not null
                ? $"Original ({width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}×{height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
                : "Original";
        }

        var actual = width is not null && height is not null
            ? $" ({width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}×{height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : string.Empty;
        return $"{profile}p{actual}";
    }
}