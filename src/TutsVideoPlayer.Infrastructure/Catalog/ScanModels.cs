using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.Media;

namespace TutsVideoPlayer.Infrastructure.Catalog;

public sealed record DiscoveredLesson(
    string RelativePath,
    string CourseDirectory,
    string FolderDirectory,
    long LengthBytes,
    long ModifiedUtcMs,
    string? CompanionRelativePath,
    long? CompanionLengthBytes,
    long? CompanionModifiedUtcMs,
    MediaProbeResult? Probe);

public sealed record DiscoveredSubtitle(
    string RelativePath,
    string CourseDirectory,
    long LengthBytes,
    long ModifiedUtcMs);

public sealed record ScanOutcome(
    int DiscoveredLessonCount,
    int DiscoveredSubtitleCount,
    bool Succeeded);
