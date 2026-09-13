namespace TutsVideoPlayer.Web.Features.Library;

public sealed class AppOptions
{
    public const string SectionName = "App";

    public string LibraryRoot { get; init; } = string.Empty;

    public string DataDirectory { get; init; } = "appdata";

    public string FFprobePath { get; init; } = "ffprobe";

    /// <summary>
    /// Origins allowed to send mutating requests, for example
    /// <c>http://localhost:8080</c>. When empty, the configured AllowedHosts list is used.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];
}
