using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Infrastructure.Persistence.Entities;

public sealed class LibraryEntity
{
    public long Id { get; private set; }
    public string DisplayName { get; set; } = string.Empty;
    public string LogicalIdentity { get; set; } = string.Empty;
    public long? LastSuccessfulScanId { get; set; }
    public long CatalogRevision { get; set; }
    public int Revision { get; set; }
}

public sealed class CourseEntity
{
    public long Id { get; private set; }
    public long LibraryId { get; set; }
    public List<LessonEntity> Lessons { get; set; } = [];
    public string RelativeDirectory { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public string SearchTitle { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public CatalogAvailability Availability { get; set; }
    public int Revision { get; set; }
}

public sealed class LessonFolderEntity
{
    public long Id { get; private set; }
    public long CourseId { get; set; }
    public CourseEntity Course { get; set; } = null!;
    public long? ParentFolderId { get; set; }
    public LessonFolderEntity? ParentFolder { get; set; }
    public string RelativeDirectory { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
}

public sealed class LessonEntity
{
    public long Id { get; private set; }
    public long CourseId { get; set; }
    public long? FolderId { get; set; }
    public int SourceGeneration { get; set; } = 1;
    public string PrimaryRelativePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SearchTitle { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    public CatalogAvailability Availability { get; set; }
    public long? LastSeenScanId { get; set; }
    public int Revision { get; set; }

    public CourseEntity Course { get; set; } = null!;
    public LessonFolderEntity? Folder { get; set; }
    public List<SourceComponentEntity> SourceComponents { get; set; } = [];
}

public sealed class SourceComponentEntity
{
    public long Id { get; private set; }
    public long LessonId { get; set; }
    public int Generation { get; set; }
    public SourceComponentRole Role { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long LengthBytes { get; set; }
    public long ModifiedUtcMs { get; set; }
    public string? Sha256 { get; set; }
    public string? ProbeMetadata { get; set; }

    public LessonEntity Lesson { get; set; } = null!;
}

public sealed class SubtitleTrackEntity
{
    public long Id { get; private set; }
    public long CourseId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string? Language { get; set; }
    public long LengthBytes { get; set; }
    public long ModifiedUtcMs { get; set; }
    public string ParseStatus { get; set; } = "Discovered";

    public CourseEntity Course { get; set; } = null!;
}

public sealed class ScanRunEntity
{
    public long Id { get; private set; }
    public long StartedUtcMs { get; set; }
    public long? FinishedUtcMs { get; set; }
    public string State { get; set; } = "Running";
    public int DiscoveredCount { get; set; }
    public int IssueCount { get; set; }
    public long CatalogRevision { get; set; }
}

public sealed class ScanIssueEntity
{
    public long Id { get; private set; }
    public long ScanRunId { get; set; }
    public string? RelativePath { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public ScanRunEntity ScanRun { get; set; } = null!;
}
