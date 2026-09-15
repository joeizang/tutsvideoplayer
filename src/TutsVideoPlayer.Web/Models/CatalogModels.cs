using System.Text.Json.Serialization;

namespace TutsVideoPlayer.Web.Models;

public sealed record LibraryStatusModel(
    [property: JsonPropertyName("schemaReady")] bool SchemaReady,
    [property: JsonPropertyName("rootConfigured")] bool RootConfigured,
    [property: JsonPropertyName("rootAvailable")] bool RootAvailable);

public sealed record ScanStatusModel(
    [property: JsonPropertyName("scanRunId")] string ScanRunId,
    [property: JsonPropertyName("startedUtcMs")] long StartedUtcMs,
    [property: JsonPropertyName("finishedUtcMs")] long? FinishedUtcMs,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("discoveredCount")] int DiscoveredCount,
    [property: JsonPropertyName("issueCount")] int IssueCount,
    [property: JsonPropertyName("catalogRevision")] long CatalogRevision);

public sealed record ScanStartResultModel(
    [property: JsonPropertyName("scanRunId")] string ScanRunId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("joined")] bool Joined);

public sealed record ScanIssueModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("relativePath")] string? RelativePath,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record LibrarySummaryModel(
    [property: JsonPropertyName("libraryName")] string LibraryName,
    [property: JsonPropertyName("logicalIdentity")] string LogicalIdentity,
    [property: JsonPropertyName("catalogRevision")] long CatalogRevision,
    [property: JsonPropertyName("courseCount")] int CourseCount,
    [property: JsonPropertyName("availableLessonCount")] int AvailableLessonCount,
    [property: JsonPropertyName("missingLessonCount")] int MissingLessonCount,
    [property: JsonPropertyName("subtitleTrackCount")] int SubtitleTrackCount,
    [property: JsonPropertyName("latestScan")] ScanStatusModel? LatestScan,
    [property: JsonPropertyName("status")] LibraryStatusModel Status);

public sealed record CourseSummaryModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("lessonCount")] int LessonCount,
    [property: JsonPropertyName("availableLessonCount")] int AvailableLessonCount,
    [property: JsonPropertyName("missingLessonCount")] int MissingLessonCount,
    [property: JsonPropertyName("completedLessonCount")] int CompletedLessonCount,
    [property: JsonPropertyName("available")] bool Available);

public sealed record CourseListModel(
    [property: JsonPropertyName("courses")] IReadOnlyList<CourseSummaryModel> Courses,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("query")] string? Query);

public sealed record CourseTreeNodeModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("sortKey")] string SortKey,
    [property: JsonPropertyName("folderId")] string? FolderId,
    [property: JsonPropertyName("available")] bool? Available,
    [property: JsonPropertyName("completed")] bool? Completed,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("durationMs")] long? DurationMs);

public sealed record CourseTreeModel(
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("courseTitle")] string CourseTitle,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("nodes")] IReadOnlyList<CourseTreeNodeModel> Nodes);

public sealed record ProbeSummaryModel(
    [property: JsonPropertyName("videoCodec")] string? VideoCodec,
    [property: JsonPropertyName("audioCodec")] string? AudioCodec,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height);

public sealed record LessonDetailModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("courseId")] string CourseId,
    [property: JsonPropertyName("courseTitle")] string CourseTitle,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("availability")] string Availability,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("sourceGeneration")] int SourceGeneration,
    [property: JsonPropertyName("probe")] ProbeSummaryModel? Probe,
    [property: JsonPropertyName("subtitleCandidateCount")] int SubtitleCandidateCount,
    [property: JsonPropertyName("previousLessonId")] string? PreviousLessonId,
    [property: JsonPropertyName("nextLessonId")] string? NextLessonId);

public sealed record LibraryHomeModel(
    [property: JsonPropertyName("summary")] LibrarySummaryModel Summary,
    [property: JsonPropertyName("courses")] IReadOnlyList<CourseSummaryModel> Courses,
    [property: JsonPropertyName("continueEntries")] IReadOnlyList<ContinueLearningEntryModel> ContinueEntries,
    [property: JsonPropertyName("queue")] QueueSummaryModel Queue,
    [property: JsonPropertyName("searchQuery")] string? SearchQuery,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("matchingCourseCount")] int MatchingCourseCount);

public sealed record WatchLessonModel(
    [property: JsonPropertyName("lesson")] LessonDetailModel Lesson,
    [property: JsonPropertyName("rail")] CourseTreeModel Rail,
    [property: JsonPropertyName("manifest")] PlaybackManifestModel? Manifest);
