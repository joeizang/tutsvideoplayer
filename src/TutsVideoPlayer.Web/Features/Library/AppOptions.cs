namespace TutsVideoPlayer.Web.Features.Library;

public sealed class AppOptions
{
    public const string SectionName = "App";

    public string LibraryRoot { get; init; } = string.Empty;

    public string DataDirectory { get; init; } = "appdata";

    public string FFprobePath { get; init; } = "ffprobe";
}
