using System.Text.RegularExpressions;

namespace TutsVideoPlayer.Infrastructure.Catalog;

public static partial class DisplayTitle
{
    [GeneratedRegex(@"^\d+[\s._-]+")]
    private static partial Regex OrderingPrefix();

    public static string FromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Strip(stem);
    }

    public static string FromFolderName(string folderName) => Strip(folderName);

    private static string Strip(string stem)
    {
        var stripped = OrderingPrefix().Replace(stem, string.Empty).Trim();
        return stripped.Length > 0 ? stripped : stem;
    }

    public static string NormalizeForSearch(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}