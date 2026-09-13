namespace TutsVideoPlayer.Core.Catalog;

public sealed record RelativeMediaPath
{
    private RelativeMediaPath(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string candidate, out RelativeMediaPath? path)
    {
        path = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (Path.IsPathRooted(candidate))
        {
            return false;
        }

        if (candidate.Contains('\0') || (OperatingSystem.IsWindows() && candidate.Contains(':')))
        {
            return false;
        }

        var segments = candidate.Split(Separators, StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        path = new RelativeMediaPath(candidate);
        return true;
    }

    public static RelativeMediaPath Create(string candidate) =>
        TryCreate(candidate, out var path)
            ? path
            : throw new ArgumentException($"'{candidate}' is not a valid library-relative media path.", nameof(candidate));

    private static readonly char[] Separators =
    [
        Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? Path.AltDirectorySeparatorChar : '\0'
    ];

    public IReadOnlyList<string> Segments => Value.Split(Separators, StringSplitOptions.None);

    public override string ToString() => Value;
}