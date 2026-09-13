using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Catalog;

public sealed class LibraryScanner(
    AppDbContext context,
    ILibraryFileEnumerator enumerator,
    FFprobeAdapter probeAdapter,
    ILogger<LibraryScanner> logger)
{
    private const int IssueStorageLimit = 200;
    private static readonly JsonSerializerOptions ProbeJsonOptions = JsonSerializerOptions.Default;

    public async Task<ScanOutcome> ExecuteAsync(
        long scanRunId,
        LibraryEntity library,
        string root,
        CancellationToken cancellationToken = default)
    {
        var enumeration = enumerator.Enumerate(root, cancellationToken);

        if (enumeration.FailedDirectories.Contains(string.Empty))
        {
            var issue = enumeration.Issues.FirstOrDefault(i => i.Code == "RootUnavailable")
                ?? new LibraryEnumerationIssue(string.Empty, "RootUnavailable", "The library root is unavailable.");
            await RecordIssuesAndFinishAsync(scanRunId, "Failed", [issue], cancellationToken);
            return new ScanOutcome(0, 0, Succeeded: false);
        }

        var discovered = Discover(enumeration);
        var issueBuffer = new IssueBuffer(enumeration.Issues);
        await ProbeChangedSourcesAsync(discovered.Lessons, root, issueBuffer, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var existing = await LoadExistingCatalogAsync(library.Id, cancellationToken);

            await ReconcileCoursesAndFoldersAsync(library.Id, scanRunId, discovered, existing, cancellationToken);
            var (availableCount, missingCount) = await ReconcileLessonsAsync(scanRunId, discovered, existing, enumeration, cancellationToken);
            await ReconcileSubtitlesAsync(scanRunId, discovered, existing, enumeration, cancellationToken);
            await ReconcileCourseAvailabilityAsync(library.Id, cancellationToken);

            library.LastSuccessfulScanId = scanRunId;
            library.CatalogRevision++;
            library.Revision++;

            issueBuffer.Add(new LibraryEnumerationIssue(
                null,
                "ScanSummary",
                $"Discovered {discovered.Lessons.Count} lessons ({availableCount} available, {missingCount} marked missing) and {discovered.Subtitles.Count} subtitle candidates."));
            await PersistIssuesAsync(scanRunId, issueBuffer, cancellationToken);

            var run = await context.ScanRuns.SingleAsync(r => r.Id == scanRunId, cancellationToken);
            run.DiscoveredCount = discovered.Lessons.Count;
            run.IssueCount = issueBuffer.Count;
            run.CatalogRevision = library.CatalogRevision;
            run.State = "Succeeded";
            run.FinishedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Scan {ScanRunId} discovered {LessonCount} lessons and {SubtitleCount} subtitle candidates with {IssueCount} issues.",
                scanRunId, discovered.Lessons.Count, discovered.Subtitles.Count, issueBuffer.Count);

            return new ScanOutcome(discovered.Lessons.Count, discovered.Subtitles.Count, Succeeded: true);
        }
        catch (OperationCanceledException)
        {
            await AbortRunAsync(scanRunId, "Canceled", cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scan {ScanRunId} failed.", scanRunId);
            await AbortRunAsync(scanRunId, "Failed", cancellationToken, exception.Message);
            return new ScanOutcome(0, 0, Succeeded: false);
        }
    }

    private Discovery Discover(LibraryEnumeration enumeration)
    {
        var issues = enumeration.Issues;
        var reservedDirs = new HashSet<string>(StringComparer.Ordinal);
        var lessons = new Dictionary<string, DiscoveredLesson>(StringComparer.Ordinal);
        var subtitles = new List<DiscoveredSubtitle>();
        var companionCandidates = new Dictionary<string, LibraryFileEntry>(StringComparer.Ordinal);
        var videoFilesByPath = new Dictionary<string, LibraryFileEntry>(StringComparer.Ordinal);

        foreach (var file in enumeration.Files)
        {
            var segments = file.RelativePath.Split('/');
            string? reservedAncestor = null;
            foreach (var segment in segments[..^1])
            {
                if (MediaFileClassification.IsReservedDirectory(segment))
                {
                    reservedAncestor = segment;
                    break;
                }
            }

            if (reservedAncestor is not null)
            {
                continue;
            }

            var directory = segments.Length == 1 ? string.Empty : string.Join('/', segments[..^1]);
            var fileName = segments[^1];

            switch (MediaFileClassification.Classify(fileName))
            {
                case MediaFileKind.Video:
                    videoFilesByPath[file.RelativePath] = file;
                    break;
                case MediaFileKind.CompanionAudio:
                    companionCandidates[file.RelativePath] = file;
                    break;
                case MediaFileKind.Subtitle:
                    subtitles.Add(new DiscoveredSubtitle(
                        file.RelativePath,
                        segments[0],
                        file.LengthBytes,
                        file.ModifiedUtcMs));
                    break;
            }
        }

        foreach (var video in videoFilesByPath.Values)
        {
            var segments = video.RelativePath.Split('/');
            if (segments.Length == 1)
            {
                issues.Add(new LibraryEnumerationIssue(
                    video.RelativePath,
                    "RootLevelVideo",
                    "Loose videos at the library root are not organized into a course in this version."));
                continue;
            }

            var directory = string.Join('/', segments[..^1]);
            var courseDirectory = segments[0];
            string? companionPath = null;
            long? companionLength = null;
            long? companionModified = null;

            if (video.RelativePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                var companionName = MediaFileClassification.CompanionAudioNameFor(segments[^1]);
                var companionCandidate = $"{directory}/{companionName}";
                if (companionCandidates.TryGetValue(companionCandidate, out var companion))
                {
                    companionPath = companion.RelativePath;
                    companionLength = companion.LengthBytes;
                    companionModified = companion.ModifiedUtcMs;
                }
                else
                {
                    issues.Add(new LibraryEnumerationIssue(
                        video.RelativePath,
                        "MissingCompanionAudio",
                        "This MPEG-TS lesson has no matching _audio.aac companion in the same directory."));
                }
            }

            lessons[video.RelativePath] = new DiscoveredLesson(
                video.RelativePath,
                courseDirectory,
                directory,
                video.LengthBytes,
                video.ModifiedUtcMs,
                companionPath,
                companionLength,
                companionModified,
                Probe: null);
        }

        foreach (var orphan in companionCandidates.Keys.Except(
                     lessons.Values.Where(l => l.CompanionRelativePath is not null).Select(l => l.CompanionRelativePath!)))
        {
            issues.Add(new LibraryEnumerationIssue(
                orphan,
                "OrphanCompanionAudio",
                "This companion audio file has no matching video lesson in the same directory."));
        }

        var lessonCourseDirectories = lessons.Values.Select(l => l.CourseDirectory).ToHashSet(StringComparer.Ordinal);
        subtitles.RemoveAll(subtitle => !lessonCourseDirectories.Contains(subtitle.CourseDirectory));

        return new Discovery(lessons, subtitles);
    }

    private async Task ProbeChangedSourcesAsync(
        Dictionary<string, DiscoveredLesson> discoveredLessons,
        string root,
        IssueBuffer issueBuffer,
        CancellationToken cancellationToken)
    {
        var existingComponents = await context.SourceComponents
            .Where(component => discoveredLessons.Keys.Contains(component.RelativePath))
            .Select(component => new
            {
                component.RelativePath,
                component.Generation,
                component.Role,
                component.LengthBytes,
                component.ModifiedUtcMs,
                LessonGeneration = component.Lesson.SourceGeneration
            })
            .ToListAsync(cancellationToken);

        var existingVideos = existingComponents
            .Where(component => component.Role == SourceComponentRole.Video)
            .ToDictionary(component => component.RelativePath);

        foreach (var lesson in discoveredLessons.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingVideo = existingVideos.GetValueOrDefault(lesson.RelativePath);
            var needsProbe = existingVideo is null
                || existingVideo.LengthBytes != lesson.LengthBytes
                || existingVideo.ModifiedUtcMs != lesson.ModifiedUtcMs;

            if (!needsProbe)
            {
                continue;
            }

            var absolutePath = Path.Combine(root, lesson.RelativePath);
            var probe = await probeAdapter.ProbeAsync(absolutePath, cancellationToken);
            if (probe.Success)
            {
                discoveredLessons[lesson.RelativePath] = lesson with { Probe = probe };
            }
            else
            {
                issueBuffer.Add(new LibraryEnumerationIssue(
                    lesson.RelativePath,
                    "ProbeFailed",
                    probe.Error ?? "The media file could not be probed."));
            }
        }
    }

    private sealed record ExistingCatalog(
        Dictionary<string, CourseEntity> CoursesByDirectory,
        Dictionary<string, LessonFolderEntity> FoldersByDirectory,
        Dictionary<string, LessonEntity> LessonsByPath,
        Dictionary<string, SubtitleTrackEntity> SubtitlesByPath,
        Dictionary<(long LessonId, int Generation), List<SourceComponentEntity>> ComponentsByLessonGeneration);

    private async Task<ExistingCatalog> LoadExistingCatalogAsync(long libraryId, CancellationToken cancellationToken)
    {
        var courses = await context.Courses
            .Where(course => course.LibraryId == libraryId)
            .ToDictionaryAsync(course => course.RelativeDirectory, cancellationToken);
        var folders = await context.LessonFolders
            .Where(folder => courses.Keys.Contains(folder.Course.RelativeDirectory))
            .ToDictionaryAsync(folder => folder.RelativeDirectory, cancellationToken);
        var lessons = await context.Lessons
            .Include(lesson => lesson.SourceComponents)
            .Where(lesson => lesson.Course.LibraryId == libraryId)
            .ToDictionaryAsync(lesson => lesson.PrimaryRelativePath, cancellationToken);
        var subtitles = await context.SubtitleTracks
            .Where(track => courses.Keys.Contains(track.Course.RelativeDirectory))
            .ToDictionaryAsync(track => track.RelativePath, cancellationToken);

        var components = lessons.Values
            .SelectMany(lesson => lesson.SourceComponents.Select(component => (lesson, component)))
            .GroupBy(pair => (pair.lesson.Id, pair.component.Generation))
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.component).ToList());

        return new ExistingCatalog(courses, folders, lessons, subtitles, components);
    }

    private async Task ReconcileCoursesAndFoldersAsync(
        long libraryId,
        long scanRunId,
        Discovery discovered,
        ExistingCatalog existing,
        CancellationToken cancellationToken)
    {
        var courseDirectories = discovered.Lessons.Values.Select(lesson => lesson.CourseDirectory).ToHashSet(StringComparer.Ordinal);

        foreach (var directory in courseDirectories)
        {
            if (!existing.CoursesByDirectory.TryGetValue(directory, out var course))
            {
                course = new CourseEntity { LibraryId = libraryId, RelativeDirectory = directory };
                context.Courses.Add(course);
                existing.CoursesByDirectory[directory] = course;
            }

            var title = DisplayTitle.FromFolderName(directory);
            course.DisplayTitle = title;
            course.SearchTitle = $"{DisplayTitle.NormalizeForSearch(title)} {DisplayTitle.NormalizeForSearch(directory)}";
            course.SortKey = NaturalSortKey.Generate(directory);
            course.Availability = CatalogAvailability.Available;
            course.Revision++;
        }

        foreach (var course in existing.CoursesByDirectory.Values.Where(course => !courseDirectories.Contains(course.RelativeDirectory)))
        {
            course.Availability = CatalogAvailability.Missing;
        }

        await context.SaveChangesAsync(cancellationToken);

        var folderDirectories = discovered.Lessons.Values
            .Select(lesson => lesson.FolderDirectory)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var directory in folderDirectories.OrderBy(directory => directory, StringComparer.Ordinal))
        {
            var segments = directory.Split('/');
            var courseDirectory = segments[0];
            var course = existing.CoursesByDirectory[courseDirectory];

            if (!existing.FoldersByDirectory.TryGetValue(directory, out var folder))
            {
                folder = new LessonFolderEntity { CourseId = course.Id, RelativeDirectory = directory };
                context.LessonFolders.Add(folder);
                existing.FoldersByDirectory[directory] = folder;
            }

            folder.Title = DisplayTitle.FromFolderName(segments[^1]);
            folder.SortKey = NaturalSortKey.Generate(directory);
        }

        await context.SaveChangesAsync(cancellationToken);

        foreach (var directory in folderDirectories)
        {
            var segments = directory.Split('/');
            if (segments.Length <= 2)
            {
                continue;
            }

            var parentDirectory = string.Join('/', segments[..^1]);
            var folder = existing.FoldersByDirectory[directory];
            folder.ParentFolderId = existing.FoldersByDirectory[parentDirectory].Id;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int Available, int Missing)> ReconcileLessonsAsync(
        long scanRunId,
        Discovery discovered,
        ExistingCatalog existing,
        LibraryEnumeration enumeration,
        CancellationToken cancellationToken)
    {
        var folderIdsByDirectory = existing.FoldersByDirectory.ToDictionary(pair => pair.Key, pair => pair.Value.Id);
        var availableCount = 0;
        var missingCount = 0;

        foreach (var discoveredLesson in discovered.Lessons.Values)
        {
            var course = existing.CoursesByDirectory[discoveredLesson.CourseDirectory];
            if (!existing.LessonsByPath.TryGetValue(discoveredLesson.RelativePath, out var lesson))
            {
                lesson = new LessonEntity { CourseId = course.Id, PrimaryRelativePath = discoveredLesson.RelativePath };
                context.Lessons.Add(lesson);
                existing.LessonsByPath[discoveredLesson.RelativePath] = lesson;
            }

            lesson.CourseId = course.Id;
            lesson.FolderId = folderIdsByDirectory.GetValueOrDefault(discoveredLesson.FolderDirectory);
            lesson.Title = DisplayTitle.FromFileName(discoveredLesson.RelativePath);
            lesson.SearchTitle = $"{DisplayTitle.NormalizeForSearch(lesson.Title)} {DisplayTitle.NormalizeForSearch(Path.GetFileName(discoveredLesson.RelativePath))}";
            lesson.SortKey = NaturalSortKey.Generate(discoveredLesson.RelativePath);
            lesson.Availability = CatalogAvailability.Available;
            lesson.LastSeenScanId = scanRunId;
            lesson.Revision++;
            availableCount++;

            var components = existing.ComponentsByLessonGeneration.GetValueOrDefault((lesson.Id, lesson.SourceGeneration)) ?? [];
            var videoComponent = components.FirstOrDefault(component => component.Role == SourceComponentRole.Video);
            var companionComponent = components.FirstOrDefault(component => component.Role == SourceComponentRole.CompanionAudio);

            if (videoComponent is null)
            {
                lesson.DurationMs = discoveredLesson.Probe?.DurationMs;
                context.SourceComponents.Add(new SourceComponentEntity
                {
                    Lesson = lesson,
                    Generation = lesson.SourceGeneration,
                    Role = SourceComponentRole.Video,
                    RelativePath = discoveredLesson.RelativePath,
                    LengthBytes = discoveredLesson.LengthBytes,
                    ModifiedUtcMs = discoveredLesson.ModifiedUtcMs,
                    ProbeMetadata = SerializeProbe(discoveredLesson.Probe)
                });

                if (discoveredLesson.CompanionRelativePath is not null)
                {
                    context.SourceComponents.Add(new SourceComponentEntity
                    {
                        Lesson = lesson,
                        Generation = lesson.SourceGeneration,
                        Role = SourceComponentRole.CompanionAudio,
                        RelativePath = discoveredLesson.CompanionRelativePath,
                        LengthBytes = discoveredLesson.CompanionLengthBytes!.Value,
                        ModifiedUtcMs = discoveredLesson.CompanionModifiedUtcMs!.Value
                    });
                }

                continue;
            }

            var videoChanged = videoComponent.LengthBytes != discoveredLesson.LengthBytes
                || ProbeDiffers(videoComponent, discoveredLesson.Probe);
            var companionChanged = companionComponent is not null
                && discoveredLesson.CompanionRelativePath is not null
                && (companionComponent.RelativePath != discoveredLesson.CompanionRelativePath
                    || companionComponent.LengthBytes != discoveredLesson.CompanionLengthBytes);

            if (videoChanged || companionChanged)
            {
                lesson.SourceGeneration++;
                lesson.DurationMs = discoveredLesson.Probe?.DurationMs;
                context.SourceComponents.Add(new SourceComponentEntity
                {
                    Lesson = lesson,
                    Generation = lesson.SourceGeneration,
                    Role = SourceComponentRole.Video,
                    RelativePath = discoveredLesson.RelativePath,
                    LengthBytes = discoveredLesson.LengthBytes,
                    ModifiedUtcMs = discoveredLesson.ModifiedUtcMs,
                    ProbeMetadata = SerializeProbe(discoveredLesson.Probe)
                });
                if (discoveredLesson.CompanionRelativePath is not null)
                {
                    context.SourceComponents.Add(new SourceComponentEntity
                    {
                        Lesson = lesson,
                        Generation = lesson.SourceGeneration,
                        Role = SourceComponentRole.CompanionAudio,
                        RelativePath = discoveredLesson.CompanionRelativePath,
                        LengthBytes = discoveredLesson.CompanionLengthBytes!.Value,
                        ModifiedUtcMs = discoveredLesson.CompanionModifiedUtcMs!.Value
                    });
                }
            }
            else
            {
                videoComponent.LengthBytes = discoveredLesson.LengthBytes;
                videoComponent.ModifiedUtcMs = discoveredLesson.ModifiedUtcMs;
                if (discoveredLesson.Probe is not null)
                {
                    videoComponent.ProbeMetadata = SerializeProbe(discoveredLesson.Probe);
                    lesson.DurationMs ??= discoveredLesson.Probe.DurationMs;
                }

                if (companionComponent is not null && discoveredLesson.CompanionRelativePath is not null)
                {
                    companionComponent.LengthBytes = discoveredLesson.CompanionLengthBytes!.Value;
                    companionComponent.ModifiedUtcMs = discoveredLesson.CompanionModifiedUtcMs!.Value;
                }
            }
        }

        foreach (var lesson in existing.LessonsByPath.Values.Where(lesson => lesson.LastSeenScanId != scanRunId))
        {
            if (IsUnderFailedDirectory(lesson.PrimaryRelativePath, enumeration.FailedDirectories))
            {
                continue;
            }

            lesson.Availability = CatalogAvailability.Missing;
            lesson.LastSeenScanId = scanRunId;
            lesson.Revision++;
            missingCount++;
        }

        await context.SaveChangesAsync(cancellationToken);
        return (availableCount, missingCount);
    }

    private async Task ReconcileSubtitlesAsync(
        long scanRunId,
        Discovery discovered,
        ExistingCatalog existing,
        LibraryEnumeration enumeration,
        CancellationToken cancellationToken)
    {
        var discoveredByPath = discovered.Subtitles.ToDictionary(subtitle => subtitle.RelativePath, StringComparer.Ordinal);
        var courseIdsByDirectory = existing.CoursesByDirectory.ToDictionary(pair => pair.Key, pair => pair.Value.Id);

        foreach (var subtitle in discovered.Subtitles)
        {
            if (!existing.SubtitlesByPath.TryGetValue(subtitle.RelativePath, out var track))
            {
                track = new SubtitleTrackEntity { RelativePath = subtitle.RelativePath };
                context.SubtitleTracks.Add(track);
                existing.SubtitlesByPath[subtitle.RelativePath] = track;
            }

            track.CourseId = courseIdsByDirectory[subtitle.CourseDirectory];
            track.Format = Path.GetExtension(subtitle.RelativePath).ToLowerInvariant() == ".vtt" ? "Vtt" : "Srt";
            track.LengthBytes = subtitle.LengthBytes;
            track.ModifiedUtcMs = subtitle.ModifiedUtcMs;
            track.ParseStatus = "Discovered";
        }

        foreach (var track in existing.SubtitlesByPath.Values)
        {
            if (discoveredByPath.ContainsKey(track.RelativePath))
            {
                continue;
            }

            if (IsUnderFailedDirectory(track.RelativePath, enumeration.FailedDirectories))
            {
                continue;
            }

            context.SubtitleTracks.Remove(track);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileCourseAvailabilityAsync(long libraryId, CancellationToken cancellationToken)
    {
        var courseAvailability = await context.Lessons
            .Where(lesson => lesson.Course.LibraryId == libraryId)
            .GroupBy(lesson => new { lesson.CourseId, lesson.Availability })
            .Select(group => new { group.Key.CourseId, group.Key.Availability })
            .ToListAsync(cancellationToken);

        var availableCourseIds = courseAvailability
            .Where(group => group.Availability == CatalogAvailability.Available)
            .Select(group => group.CourseId)
            .ToHashSet();

        var courses = await context.Courses
            .Where(course => course.LibraryId == libraryId)
            .ToListAsync(cancellationToken);

        foreach (var course in courses)
        {
            course.Availability = availableCourseIds.Contains(course.Id)
                ? CatalogAvailability.Available
                : CatalogAvailability.Missing;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistIssuesAsync(long scanRunId, IssueBuffer buffer, CancellationToken cancellationToken)
    {
        foreach (var issue in buffer.Take(IssueStorageLimit))
        {
            context.ScanIssues.Add(new ScanIssueEntity
            {
                ScanRunId = scanRunId,
                RelativePath = issue.RelativeDirectory,
                Code = issue.Code,
                Message = issue.Message
            });
        }

        if (buffer.Count > IssueStorageLimit)
        {
            context.ScanIssues.Add(new ScanIssueEntity
            {
                ScanRunId = scanRunId,
                RelativePath = null,
                Code = "IssuesTruncated",
                Message = $"{buffer.Count - IssueStorageLimit} additional issues were not stored in this bounded list."
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordIssuesAndFinishAsync(
        long scanRunId,
        string state,
        IReadOnlyList<LibraryEnumerationIssue> issues,
        CancellationToken cancellationToken)
    {
        var run = await context.ScanRuns.SingleAsync(r => r.Id == scanRunId, cancellationToken);
        run.State = state;
        run.FinishedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        run.IssueCount = issues.Count;

        foreach (var issue in issues)
        {
            context.ScanIssues.Add(new ScanIssueEntity
            {
                ScanRunId = scanRunId,
                RelativePath = issue.RelativeDirectory,
                Code = issue.Code,
                Message = issue.Message
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task AbortRunAsync(long scanRunId, string state, CancellationToken cancellationToken, string? message = null)
    {
        try
        {
            var run = await context.ScanRuns.SingleAsync(r => r.Id == scanRunId, CancellationToken.None);
            run.State = state;
            run.FinishedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message is not null)
            {
                context.ScanIssues.Add(new ScanIssueEntity
                {
                    ScanRunId = scanRunId,
                    RelativePath = null,
                    Code = "ScanAborted",
                    Message = message
                });
            }

            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
        {
        }
    }

    private static bool IsUnderFailedDirectory(string relativePath, HashSet<string> failedDirectories)
    {
        var segments = relativePath.Split('/');
        for (var take = 0; take < segments.Length; take++)
        {
            var ancestor = take == 0 ? string.Empty : string.Join('/', segments[..take]);
            if (failedDirectories.Contains(ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ProbeDiffers(SourceComponentEntity component, MediaProbeResult? probe)
    {
        if (probe is null || component.ProbeMetadata is null)
        {
            return false;
        }

        ProbeMetadata? stored;
        try
        {
            stored = JsonSerializer.Deserialize<ProbeMetadata>(component.ProbeMetadata, ProbeJsonOptions);
        }
        catch (JsonException)
        {
            return true;
        }

        if (stored is null)
        {
            return true;
        }

        return stored.DurationMs != probe.DurationMs
            || stored.VideoCodec != probe.VideoCodec
            || stored.AudioCodec != probe.AudioCodec
            || stored.Width != probe.Width
            || stored.Height != probe.Height;
    }

    private static string? SerializeProbe(MediaProbeResult? probe) =>
        probe is null
            ? null
            : JsonSerializer.Serialize(
                new ProbeMetadata(probe.DurationMs, probe.VideoCodec, probe.AudioCodec, probe.Width, probe.Height),
                ProbeJsonOptions);

    private sealed record Discovery(
        Dictionary<string, DiscoveredLesson> Lessons,
        List<DiscoveredSubtitle> Subtitles);

    private sealed class IssueBuffer
    {
        private readonly List<LibraryEnumerationIssue> _issues;

        public IssueBuffer(List<LibraryEnumerationIssue> issues) => _issues = issues;

        public int Count => _issues.Count;

        public void Add(LibraryEnumerationIssue issue) => _issues.Add(issue);

        public IEnumerable<LibraryEnumerationIssue> Take(int limit) => _issues.Take(limit);
    }
}
