using System.Text.Json.Serialization;

namespace TutsVideoPlayer.Web.Models;

public sealed record ProgressStateModel(
    [property: JsonPropertyName("positionMs")] long PositionMs,
    [property: JsonPropertyName("effectiveCompletion")] bool EffectiveCompletion,
    [property: JsonPropertyName("manualCompletion")] string? ManualCompletion,
    [property: JsonPropertyName("revision")] int Revision);

public sealed record RenditionModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("codecHint")] string? CodecHint,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("retentionClass")] string RetentionClass,
    [property: JsonPropertyName("mediaUrl")] string? MediaUrl);

public sealed record PlaybackPreferencesModel(
    [property: JsonPropertyName("speed")] double Speed,
    [property: JsonPropertyName("autoplay")] bool Autoplay,
    [property: JsonPropertyName("fitMode")] string FitMode,
    // Carried so the player can send an If-Match precondition with its first settings write.
    [property: JsonPropertyName("revision")] int Revision);

public sealed record ManifestSubtitleModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("trackUrl")] string? TrackUrl);

public sealed record SubtitleSelectionStateModel(
    [property: JsonPropertyName("selectedSubtitleId")] string? SelectedSubtitleId,
    [property: JsonPropertyName("subtitlesEnabled")] bool SubtitlesEnabled);

public sealed record PlaybackManifestModel(
    [property: JsonPropertyName("lessonId")] string LessonId,
    [property: JsonPropertyName("sourceGeneration")] int SourceGeneration,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("progress")] ProgressStateModel Progress,
    [property: JsonPropertyName("renditions")] IReadOnlyList<RenditionModel> Renditions,
    [property: JsonPropertyName("subtitles")] IReadOnlyList<ManifestSubtitleModel> Subtitles,
    [property: JsonPropertyName("selection")] SubtitleSelectionStateModel Selection,
    [property: JsonPropertyName("readyDefaultRenditionId")] string? ReadyDefaultRenditionId,
    [property: JsonPropertyName("preferences")] PlaybackPreferencesModel Preferences);

public sealed record PlaybackSessionStartModel(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("progressRevision")] int ProgressRevision,
    [property: JsonPropertyName("savedPositionMs")] long SavedPositionMs,
    [property: JsonPropertyName("lastSequence")] long LastSequence,
    [property: JsonPropertyName("heartbeatIntervalMs")] int HeartbeatIntervalMs);

public sealed record HeartbeatModel(
    [property: JsonPropertyName("activeRenditionId")] string? ActiveRenditionId);

public sealed record HeartbeatResultModel(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("serverUtcMs")] long ServerUtcMs);

public sealed record ProgressWriteModel(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("sourceGeneration")] int SourceGeneration,
    [property: JsonPropertyName("positionMs")] long PositionMs,
    [property: JsonPropertyName("isPlaying")] bool IsPlaying,
    [property: JsonPropertyName("ended")] bool Ended);

public sealed record ProgressWriteResultModel(
    [property: JsonPropertyName("acceptedPositionMs")] long AcceptedPositionMs,
    [property: JsonPropertyName("effectiveCompletion")] bool EffectiveCompletion,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("duplicate")] bool Duplicate);

public sealed record SessionCloseModel(
    [property: JsonPropertyName("sequence")] long? Sequence,
    [property: JsonPropertyName("positionMs")] long? PositionMs);

public sealed record SessionCloseResultModel(
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("progressFlushed")] bool ProgressFlushed);

public sealed record CompletionRequestModel(
    [property: JsonPropertyName("choice")] string Choice);

public sealed record CompletionResultModel(
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("effectiveCompletion")] bool EffectiveCompletion,
    [property: JsonPropertyName("revision")] int Revision);

public sealed record ContinueLearningEntryModel(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("courseTitle")] string CourseTitle,
    [property: JsonPropertyName("lessonId")] string LessonId,
    [property: JsonPropertyName("lessonTitle")] string LessonTitle,
    [property: JsonPropertyName("positionMs")] long PositionMs,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("completedLessons")] int CompletedLessons,
    [property: JsonPropertyName("totalLessons")] int TotalLessons,
    [property: JsonPropertyName("recommendedLessonId")] string RecommendedLessonId,
    // "resume", "next", "replay", or "unavailable" when nothing in the course can be played.
    [property: JsonPropertyName("recommendation")] string Recommendation,
    [property: JsonPropertyName("lessonAvailable")] bool LessonAvailable,
    [property: JsonPropertyName("sourceChanged")] bool SourceChanged);

public sealed record SettingsModel(
    [property: JsonPropertyName("playbackSpeed")] double PlaybackSpeed,
    [property: JsonPropertyName("autoplay")] bool Autoplay,
    [property: JsonPropertyName("fitMode")] string FitMode,
    [property: JsonPropertyName("subtitleEnabled")] bool SubtitleEnabled,
    [property: JsonPropertyName("revision")] int Revision);

public sealed record SettingsUpdateModel(
    [property: JsonPropertyName("playbackSpeed")] double? PlaybackSpeed,
    [property: JsonPropertyName("autoplay")] bool? Autoplay,
    [property: JsonPropertyName("fitMode")] string? FitMode,
    [property: JsonPropertyName("subtitleEnabled")] bool? SubtitleEnabled);
