namespace TutsVideoPlayer.Web.Features.Library;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Relative paths and ~/ paths are resolved against the current user's home directory.</summary>
    public string LibraryRoot
    {
        get;
        init
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value))
            {
                field = value;
                return;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException("Cannot resolve App:LibraryRoot because the current user's home directory is unavailable. Configure an absolute path.");
            }

            var relative = value == "~" ? string.Empty
                : value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal)
                    ? value[2..] : value;
            field = Path.GetFullPath(relative.Length == 0 ? "." : relative, home);
        }
    } = string.Empty;

    public string DataDirectory { get; init; } = "appdata";

    public string FFprobePath { get; init; } = "ffprobe";

    /// <summary>
    /// Origins allowed to send mutating requests, for example
    /// <c>http://localhost:8080</c>. When empty, the configured AllowedHosts list is used.
    /// </summary>
    public string[] AllowedOrigins { get; init; } = [];
}
