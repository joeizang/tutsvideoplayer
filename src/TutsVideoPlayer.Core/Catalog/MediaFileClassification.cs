namespace TutsVideoPlayer.Core.Catalog;

public enum MediaFileKind
{
    Ignored,
    Video,
    CompanionAudio,
    Subtitle
}

public static class MediaFileClassification
{
    public const string ReservedAppDirectory = ".tutsvideoplayer";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".wmv", ".ts", ".mov", ".m4v", ".avi", ".webm", ".mpg", ".mpeg", ".mts", ".m2ts", ".flv"
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".vtt"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".part", ".download", ".crdownload", ".tmp", ".torrent", ".nfo", ".txt", ".md", ".url", ".html", ".jpg", ".jpeg", ".png", ".gif", ".db"
    };

    private const string CompanionAudioSuffix = "_audio";

    public static bool IsReservedDirectory(string name) =>
        name.Equals(ReservedAppDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// App-owned permanent playback copies and their provenance manifests. They
    /// are never lessons and never unsupported files; the scanner skips them so
    /// a converted copy cannot become a second lesson.
    /// </summary>
    public static bool IsManagedOutputName(string fileName)
    {
        if (fileName.EndsWith(".tvp.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Contains(".tvp-", StringComparison.Ordinal);
    }

    public static bool IsIgnoredName(string name)
    {
        if (name.StartsWith('.'))
        {
            return true;
        }

        var extension = Path.GetExtension(name);
        return IgnoredExtensions.Contains(extension);
    }

    public static MediaFileKind Classify(string fileName)
    {
        if (IsIgnoredName(fileName))
        {
            return MediaFileKind.Ignored;
        }

        var extension = Path.GetExtension(fileName);
        if (VideoExtensions.Contains(extension))
        {
            return MediaFileKind.Video;
        }

        if (SubtitleExtensions.Contains(extension))
        {
            return MediaFileKind.Subtitle;
        }

        if (extension.Equals(".aac", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            if (stem.EndsWith(CompanionAudioSuffix, StringComparison.Ordinal))
            {
                return MediaFileKind.CompanionAudio;
            }
        }

        return MediaFileKind.Ignored;
    }

    public static string CompanionAudioNameFor(string videoFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(videoFileName);
        return $"{stem}_audio.aac";
    }

    public static string VideoStemFor(string companionAudioFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(companionAudioFileName);
        return stem.EndsWith(CompanionAudioSuffix, StringComparison.Ordinal)
            ? stem[..^CompanionAudioSuffix.Length]
            : stem;
    }
}
