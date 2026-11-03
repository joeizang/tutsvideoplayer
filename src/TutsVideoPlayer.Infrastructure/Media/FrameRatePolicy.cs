using System.Globalization;

namespace TutsVideoPlayer.Infrastructure.Media;

public static class FrameRatePolicy
{
    // Rate declarations alone are unreliable. Check presentation timestamps, sorted
    // because H.264 B-frames are stored in decode order. Allow container rounding.
    public static bool HasIrregularTiming(IEnumerable<double> timestamps, string? timeBase = null)
    {
        var ordered = timestamps.Order().ToArray();
        if (ordered.Length < 3) return false;
        var gaps = ordered.Zip(ordered.Skip(1), (a, b) => b - a).ToArray();
        var typical = gaps.Order().ElementAt(gaps.Length / 2);
        // A CFR stream may alternate between adjacent tick counts (e.g. 33/34 ms
        // in Matroska). Include an epsilon for decimal-to-double rounding.
        var tolerance = Math.Max(Parse(timeBase) ?? 0.001, typical * 0.02) + 1e-9;
        return gaps.Any(gap => Math.Abs(gap - typical) > tolerance);
    }

    public static IReadOnlyList<string> EncoderArguments(ProbeMetadata? probe)
    {
        var rate = probe?.IsVariableFrameRate == true
            ? probe.AverageFrameRate
            : probe?.NominalFrameRate ?? probe?.AverageFrameRate;
        var value = Parse(rate);
        if (value is null && probe?.IsVariableFrameRate != true)
        {
            rate = probe?.AverageFrameRate;
            value = Parse(rate);
        }
        if (value is null)
            return probe?.IsVariableFrameRate == true ? ["-fps_mode", "cfr", "-r", "30"] : ["-fps_mode", "cfr"];

        if (probe?.IsVariableFrameRate == true)
        {
            // Avoid encoding a ~30 fps screen recording at its declared ~60 fps.
            string[] common = ["15", "24000/1001", "24", "25", "30000/1001", "30", "50", "60000/1001", "60"];
            var nearest = common.MinBy(candidate => Math.Abs(Parse(candidate)!.Value - value.Value))!;
            if (Math.Abs(Parse(nearest)!.Value - value.Value) / value.Value < 0.05) rate = nearest;
        }

        return ["-fps_mode", "cfr", "-r", rate!];
    }

    private static double? Parse(string? rate)
    {
        var parts = rate?.Split('/');
        if (parts is null || parts.Length is < 1 or > 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return null;
        var denominator = 1.0;
        if (parts.Length == 2
            && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator)) return null;
        var value = numerator / denominator;
        return double.IsFinite(value) && value > 0 ? value : null;
    }
}
