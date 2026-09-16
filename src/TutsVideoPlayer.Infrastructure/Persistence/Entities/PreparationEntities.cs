using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;

namespace TutsVideoPlayer.Infrastructure.Persistence.Entities;

public sealed class PreparationJobEntity
{
    public long Id { get; private set; }
    public long LessonId { get; set; }
    public int SourceGeneration { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public RenditionPurpose Purpose { get; set; }
    public string? Profile { get; set; }
    public string RecipeVersion { get; set; } = string.Empty;
    public PreparationJobState State { get; set; } = PreparationJobState.Queued;
    public int Priority { get; set; }
    public long EnqueuedUtcMs { get; set; }
    public long? StartedUtcMs { get; set; }
    public int Attempt { get; set; }
    public string? LeaseOwner { get; set; }
    public long? LeaseExpiresUtcMs { get; set; }
    public double? Progress { get; set; }
    public long? ReservedBytes { get; set; }
    public string? ErrorCode { get; set; }
    public string? UserMessage { get; set; }
    public string? OutputRelativePath { get; set; }
    public string? ManifestRelativePath { get; set; }
    public int Revision { get; set; }

    public LessonEntity Lesson { get; set; } = null!;
    public List<PreparationAttemptEntity> Attempts { get; set; } = [];
}

public sealed class PreparationAttemptEntity
{
    public long Id { get; private set; }
    public long JobId { get; set; }
    public int Attempt { get; set; }
    public long StartedUtcMs { get; set; }
    public long? FinishedUtcMs { get; set; }
    public int? ExitCode { get; set; }
    public string? SanitizedFailure { get; set; }
    public string? TempRelativePath { get; set; }

    public PreparationJobEntity Job { get; set; } = null!;
}
