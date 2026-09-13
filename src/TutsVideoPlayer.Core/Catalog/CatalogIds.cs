namespace TutsVideoPlayer.Core.Catalog;

public readonly record struct LibraryId(long Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct CourseId(long Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct LessonFolderId(long Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct LessonId(long Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct SubtitleTrackId(long Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct ScanRunId(long Value)
{
    public override string ToString() => Value.ToString();
}
