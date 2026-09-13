using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Learning;

namespace TutsVideoPlayer.Infrastructure.Persistence.Entities;

public sealed class LessonProgressEntity
{
    public long LessonId { get; set; }
    public int SourceGeneration { get; set; }
    public long PositionMs { get; set; }
    public long MaxObservedPositionMs { get; set; }
    public bool AutomaticCompleted { get; set; }
    public CompletionChoice? ManualCompletion { get; set; }
    public long? ActiveSessionId { get; set; }
    public long LastSequence { get; set; }
    public long LastWatchedUtcMs { get; set; }
    public int Revision { get; set; }

    public LessonEntity Lesson { get; set; } = null!;
}

public sealed class PlaybackSessionEntity
{
    public long Id { get; private set; }
    public long LessonId { get; set; }
    public int SourceGeneration { get; set; }
    public long StartedUtcMs { get; set; }
    public long LastHeartbeatUtcMs { get; set; }
    public long? ActiveRenditionId { get; set; }
    public long? ClosedUtcMs { get; set; }

    public LessonEntity Lesson { get; set; } = null!;
}

public sealed class RenditionEntity
{
    public long Id { get; private set; }
    public long LessonId { get; set; }
    public int SourceGeneration { get; set; }
    public RenditionPurpose Purpose { get; set; }
    public string? Profile { get; set; }
    public string? RecipeVersion { get; set; }
    public RenditionRetention RetentionClass { get; set; }
    public RenditionStatus Status { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? ManifestPath { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public long ByteLength { get; set; }
    public string? OutputHash { get; set; }
    public long? LastAccessUtcMs { get; set; }
    public int Revision { get; set; }

    public LessonEntity Lesson { get; set; } = null!;
}

public sealed class PreferenceEntity
{
    public long Id { get; private set; }
    public string? PreferredQuality { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public bool SubtitleEnabled { get; set; } = true;
    public string? PreferredLanguage { get; set; }
    public bool Autoplay { get; set; }
    public string FitMode { get; set; } = "Contain";
    public long CacheLimitBytes { get; set; } = 20_000_000_000;
    public bool QueuePaused { get; set; }
    public int Revision { get; set; }
}
