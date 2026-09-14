using System.Text;
using System.Text.RegularExpressions;

namespace TutsVideoPlayer.Core.Subtitles;

public static class SubtitleLimits
{
    public const long MaxSourceBytes = 2_000_000;
    public const int MaxCues = 10_000;
    public const int MaxCueTextChars = 2_000;
}

public sealed record SubtitleConversion(bool Success, string? WebVtt, string? Error, int CueCount)
{
    public static SubtitleConversion Fail(string error) => new(false, null, error, 0);
}

public static partial class SubtitleConverter
{
    [GeneratedRegex(@"^\s*(?<start>\d{1,2}:\d{2}(:\d{2})?[,.]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{2}(:\d{2})?[,.]\d{1,3})\s*(?<settings>.*)$")]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"^(?<stem>.+)\.(?<lang>[A-Za-z]{2,3})$")]
    private static partial Regex LanguageSuffix();

    /// <summary>
    /// Parses a conservatively allowed subset of inline WebVTT markup; anything else
    /// containing angle brackets is escaped rather than passed through.
    /// </summary>
    [GeneratedRegex(@"<(?<tag>/?(?:b|i|u|c|v)(?:\s[^>]*)?|/?\d{1,2}:\d{2}(?::\d{2})?\.\d{3})>")]
    private static partial Regex AllowedInlineTag();

    public static bool TrySplitLanguageSuffix(string stem, out string baseStem, out string? language)
    {
        var match = LanguageSuffix().Match(stem);
        if (match.Success)
        {
            baseStem = match.Groups["stem"].Value;
            language = match.Groups["lang"].Value.ToLowerInvariant();
            return true;
        }

        baseStem = stem;
        language = null;
        return false;
    }

    public static SubtitleConversion ConvertToWebVtt(string source, bool isAlreadyWebVtt)
    {
        if (source.Length > SubtitleLimits.MaxSourceBytes)
        {
            return SubtitleConversion.Fail($"The subtitle file exceeds the {SubtitleLimits.MaxSourceBytes} byte limit.");
        }

        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Contains('\0', StringComparison.Ordinal))
        {
            return SubtitleConversion.Fail("The subtitle text contains binary content.");
        }

        return isAlreadyWebVtt
            ? ConvertVtt(normalized)
            : ConvertSrt(normalized);
    }

    private static SubtitleConversion ConvertSrt(string normalized)
    {
        var blocks = normalized.Split("\n\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ConvertBlocks(blocks, requireWebVttHeader: false);
    }

    private static SubtitleConversion ConvertVtt(string normalized)
    {
        var withoutHeader = normalized;
        var newline = withoutHeader.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = newline >= 0 ? withoutHeader[..newline].Trim() : withoutHeader.Trim();
        if (!firstLine.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return SubtitleConversion.Fail("WebVTT files must begin with the WEBVTT header.");
        }

        if (firstLine.Length > 6 && !firstLine[6..].TrimStart().StartsWith("--", StringComparison.Ordinal))
        {
            return SubtitleConversion.Fail("The WEBVTT header carries unsupported metadata.");
        }

        withoutHeader = newline >= 0 ? withoutHeader[(newline + 1)..] : string.Empty;
        var blocks = withoutHeader.Split("\n\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ConvertBlocks(blocks, requireWebVttHeader: true);
    }

    private static SubtitleConversion ConvertBlocks(string[] blocks, bool requireWebVttHeader)
    {
        var output = new StringBuilder(requireWebVttHeader ? "WEBVTT\n\n" : "WEBVTT\n\n");
        var cueCount = 0;
        var untimedBlocks = 0;

        foreach (var block in blocks)
        {
            if (block.StartsWith("NOTE", StringComparison.Ordinal)
                || block.StartsWith("STYLE", StringComparison.Ordinal)
                || block.StartsWith("REGION", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = block.Split('\n');
            var timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                untimedBlocks++;
                continue;
            }

            var timing = TimingLine().Match(lines[timingIndex]);
            if (!timing.Success)
            {
                return SubtitleConversion.Fail($"Cue {cueCount + 1} has a malformed timing line.");
            }

            if (!TryParseTimestamp(timing.Groups["start"].Value, out var startMs)
                || !TryParseTimestamp(timing.Groups["end"].Value, out var endMs))
            {
                return SubtitleConversion.Fail($"Cue {cueCount + 1} has an unparseable timestamp.");
            }

            if (endMs < startMs)
            {
                return SubtitleConversion.Fail($"Cue {cueCount + 1} ends before it starts.");
            }

            var textLines = lines[(timingIndex + 1)..]
                .Select(SanitizeCueText)
                .ToList();
            if (textLines.Count == 0)
            {
                continue;
            }

            if (textLines.Sum(line => line.Length) > SubtitleLimits.MaxCueTextChars)
            {
                return SubtitleConversion.Fail($"Cue {cueCount + 1} exceeds the text length limit.");
            }

            cueCount++;
            if (cueCount > SubtitleLimits.MaxCues)
            {
                return SubtitleConversion.Fail($"The subtitle file exceeds {SubtitleLimits.MaxCues} cues.");
            }

            output.Append(FormatTimestamp(startMs))
                .Append(" --> ")
                .Append(FormatTimestamp(endMs));
            var settings = timing.Groups["settings"].Value.Trim();
            if (settings.Length > 0)
            {
                output.Append(' ').Append(settings);
            }

            output.Append('\n');
            foreach (var line in textLines)
            {
                output.Append(line).Append('\n');
            }

            output.Append('\n');
        }

        if (cueCount == 0 && untimedBlocks > 0)
        {
            return SubtitleConversion.Fail("No valid cues were found; the subtitle text appears malformed.");
        }

        return new SubtitleConversion(true, output.ToString(), null, cueCount);
    }

    private static string SanitizeCueText(string line)
    {
        var builder = new StringBuilder(line.Length);
        var index = 0;
        while (index < line.Length)
        {
            var match = AllowedInlineTag().Match(line, index);
            if (match.Success && match.Index == index)
            {
                builder.Append('<').Append(match.Groups["tag"].Value).Append('>');
                index += match.Length;
                continue;
            }

            var character = line[index];
            builder.Append(character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => character
            });
            index++;
        }

        return builder.ToString();
    }

    internal static bool TryParseTimestamp(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Split(':', ',', '.');
        if (parts is not ({ Length: 3 } or { Length: 4 }))
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var first) || first < 0)
        {
            return false;
        }

        int hours;
        int minutes;
        int seconds;
        var millisText = parts[^1].PadRight(3, '0')[..3];

        if (parts.Length == 4)
        {
            hours = first;
            if (!int.TryParse(parts[1], out minutes) || !int.TryParse(parts[2], out seconds))
            {
                return false;
            }
        }
        else
        {
            hours = 0;
            minutes = first;
            if (!int.TryParse(parts[1], out seconds))
            {
                return false;
            }
        }

        if (minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            return false;
        }

        if (!int.TryParse(millisText, out var millis))
        {
            return false;
        }

        milliseconds = ((hours * 60L + minutes) * 60 + seconds) * 1000 + millis;
        return true;
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var hours = milliseconds / 3_600_000;
        var minutes = milliseconds / 60_000 % 60;
        var seconds = milliseconds / 1000 % 60;
        var millis = milliseconds % 1000;
        return $"{hours:00}:{minutes:00}:{seconds:00}.{millis:000}";
    }
}