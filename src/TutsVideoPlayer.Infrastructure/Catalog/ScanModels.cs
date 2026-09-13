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
    MediaProbeResult? Probe,
    // Complete content digests, calculated only when the source was inspected during this
    // scan. A failed attempt is recorded separately from a skipped inspection.
    string? Sha256 = null,
    string? CompanionSha256 = null,
    bool FingerprintFailed = false);

public sealed record DiscoveredSubtitle(
    string RelativePath,
    string CourseDirectory,
    long LengthBytes,
    long ModifiedUtcMs);

public sealed record ScanOutcome(
    int DiscoveredLessonCount,
    int DiscoveredSubtitleCount,
    bool Succeeded);
