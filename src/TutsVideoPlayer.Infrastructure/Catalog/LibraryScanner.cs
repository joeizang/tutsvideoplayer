using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Subtitles;
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
        await InspectChangedSourcesAsync(discovered.Lessons, root, issueBuffer, cancellationToken);

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var existing = await LoadExistingCatalogAsync(library.Id, cancellationToken);

            await ReconcileCoursesAndFoldersAsync(library.Id, scanRunId, discovered, existing, cancellationToken);
            var (availableCount, missingCount) = await ReconcileLessonsAsync(scanRunId, discovered, existing, enumeration, cancellationToken);
            await ReconcileSourceRenditionsAsync(library.Id, discovered, existing.LessonsByPath.Values.ToList(), cancellationToken);
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
            // Roll the reconciliation back before recording the outcome, otherwise the
            // status write would be discarded with the transaction and the run would
            // stay Running forever.
            await RollbackQuietlyAsync(transaction);
            await AbortRunAsync(scanRunId, "Canceled", cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Scan {ScanRunId} failed.", scanRunId);
            await RollbackQuietlyAsync(transaction);
            await AbortRunAsync(scanRunId, "Failed", cancellationToken, exception.Message);
            return new ScanOutcome(0, 0, Succeeded: false);
        }
        finally
        {
            await transaction.DisposeAsync();
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

    private async Task InspectChangedSourcesAsync(
        Dictionary<string, DiscoveredLesson> discoveredLessons,
        string root,
        IssueBuffer issueBuffer,
        CancellationToken cancellationToken)
    {
        // Only the components of each lesson's current source generation describe what is
        // on disk today. Superseded generations keep the same relative path, so including
        // them would both duplicate keys and compare against replaced content.
        var currentComponents = await context.SourceComponents
            .Where(component => discoveredLessons.Keys.Contains(component.Lesson.PrimaryRelativePath)
                && component.Generation == component.Lesson.SourceGeneration)
            .Select(component => new CurrentComponent(
                component.Lesson.PrimaryRelativePath,
                component.Role,
                component.RelativePath,
                component.LengthBytes,
                component.ModifiedUtcMs,
                component.ProbeMetadata,
                component.Sha256))
            .ToListAsync(cancellationToken);

        var currentVideos = IndexByLessonPath(currentComponents, SourceComponentRole.Video);
        var currentCompanions = IndexByLessonPath(currentComponents, SourceComponentRole.CompanionAudio);

        foreach (var lesson in discoveredLessons.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inspected = lesson;
            var currentVideo = currentVideos.GetValueOrDefault(lesson.RelativePath);
            var videoHintsMatch = currentVideo is not null
                && string.Equals(currentVideo.RelativePath, lesson.RelativePath, StringComparison.Ordinal)
                && currentVideo.LengthBytes == lesson.LengthBytes
                && currentVideo.ModifiedUtcMs == lesson.ModifiedUtcMs;

            // Unresolved probe metadata and unknown fingerprints are retried on every scan:
            // a stored size and modification time are not a valid cache entry on their own,
            // so repairing ffprobe has to actually recover duration and codec discovery.
            var videoNeedsInspection = !videoHintsMatch
                || currentVideo!.ProbeMetadata is null
                || currentVideo.Sha256 is null;

            if (videoNeedsInspection)
            {
                var absolutePath = Path.Combine(root, lesson.RelativePath);
                var probe = await probeAdapter.ProbeAsync(absolutePath, cancellationToken);
                if (probe.Success)
                {
                    inspected = inspected with { Probe = probe };
                }
                else
                {
                    issueBuffer.Add(new LibraryEnumerationIssue(
                        lesson.RelativePath,
                        "ProbeFailed",
                        probe.Error ?? "The media file could not be probed."));
                }

                var digest = await ContentFingerprint.ComputeAsync(absolutePath, cancellationToken);
                if (digest is null)
                {
                    issueBuffer.Add(new LibraryEnumerationIssue(
                        lesson.RelativePath,
                        "FingerprintFailed",
                        "The source content digest could not be calculated; source reconciliation will be retried on the next scan."));
                }

                inspected = inspected with { Sha256 = digest, FingerprintFailed = digest is null };
            }

            if (lesson.CompanionRelativePath is not null)
            {
                var currentCompanion = currentCompanions.GetValueOrDefault(lesson.RelativePath);
                var companionHintsMatch = currentCompanion is not null
                    && string.Equals(currentCompanion.RelativePath, lesson.CompanionRelativePath, StringComparison.Ordinal)
                    && currentCompanion.LengthBytes == lesson.CompanionLengthBytes
                    && currentCompanion.ModifiedUtcMs == lesson.CompanionModifiedUtcMs;

                if (!companionHintsMatch || currentCompanion!.Sha256 is null)
                {
                    var digest = await ContentFingerprint.ComputeAsync(
                        Path.Combine(root, lesson.CompanionRelativePath), cancellationToken);
                    if (digest is null)
                    {
                        issueBuffer.Add(new LibraryEnumerationIssue(
                            lesson.CompanionRelativePath,
                            "FingerprintFailed",
                            "The companion content digest could not be calculated; source reconciliation will be retried on the next scan."));
                    }

                    inspected = inspected with
                    {
                        CompanionSha256 = digest,
                        FingerprintFailed = inspected.FingerprintFailed || digest is null
                    };
                }
            }

            discoveredLessons[lesson.RelativePath] = inspected;
        }
    }

    private static Dictionary<string, CurrentComponent> IndexByLessonPath(
        IReadOnlyList<CurrentComponent> components,
        SourceComponentRole role) =>
        components
            .Where(component => component.Role == role)
            .GroupBy(component => component.LessonPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private sealed record CurrentComponent(
        string LessonPath,
        SourceComponentRole Role,
        string RelativePath,
        long LengthBytes,
        long ModifiedUtcMs,
        string? ProbeMetadata,
        string? Sha256);

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

        // LIB-02 allows arbitrary nesting. Only directories that directly contain a lesson
        // are discovered, so every intermediate ancestor has to be materialized before the
        // hierarchy can be linked; otherwise "Course/Parent/Child/video.mp4" would create
        // Child without Parent and the parent lookup below would fail the whole scan.
        foreach (var directory in folderDirectories.ToList())
        {
            var ancestorSegments = directory.Split('/');
            for (var depth = 2; depth < ancestorSegments.Length; depth++)
            {
                folderDirectories.Add(string.Join('/', ancestorSegments[..depth]));
            }
        }

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
            if (!existing.FoldersByDirectory.TryGetValue(parentDirectory, out var parentFolder))
            {
                // Defensive: a missing ancestor must not roll back the entire catalog scan.
                logger.LogWarning(
                    "Lesson folder {Directory} has no materialized parent {ParentDirectory}; it is linked to its course directly.",
                    directory, parentDirectory);
                folder.ParentFolderId = null;
                continue;
            }

            folder.ParentFolderId = parentFolder.Id;
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
                AddSourceComponents(lesson, discoveredLesson, lesson.SourceGeneration, null, null);
                continue;
            }

            // Preserve the entire verified source set when either component could not be
            // read. Updating its hints would hide the unresolved change on the next scan,
            // and accepting only the other component would mix two different generations.
            if (discoveredLesson.FingerprintFailed)
            {
                continue;
            }

            var videoChanged = VideoContentChanged(videoComponent, discoveredLesson);
            var companionChanged = CompanionSetChanged(companionComponent, discoveredLesson);

            if (videoChanged || companionChanged)
            {
                lesson.SourceGeneration++;
                if (videoChanged || discoveredLesson.Probe is not null)
                {
                    // A companion-only change must not discard an established duration.
                    lesson.DurationMs = discoveredLesson.Probe?.DurationMs;
                }

                AddSourceComponents(
                    lesson,
                    discoveredLesson,
                    lesson.SourceGeneration,
                    videoChanged ? null : videoComponent,
                    companionChanged ? null : companionComponent);
            }
            else
            {
                videoComponent.LengthBytes = discoveredLesson.LengthBytes;
                videoComponent.ModifiedUtcMs = discoveredLesson.ModifiedUtcMs;
                videoComponent.Sha256 ??= discoveredLesson.Sha256;
                if (discoveredLesson.Probe is not null)
                {
                    videoComponent.ProbeMetadata = SerializeProbe(discoveredLesson.Probe);
                    lesson.DurationMs ??= discoveredLesson.Probe.DurationMs;
                }

                if (companionComponent is not null && discoveredLesson.CompanionRelativePath is not null)
                {
                    companionComponent.LengthBytes = discoveredLesson.CompanionLengthBytes!.Value;
                    companionComponent.ModifiedUtcMs = discoveredLesson.CompanionModifiedUtcMs!.Value;
                    companionComponent.Sha256 ??= discoveredLesson.CompanionSha256;
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

    private async Task ReconcileSourceRenditionsAsync(long libraryId, Discovery discovered, IReadOnlyList<LessonEntity> lessons, CancellationToken cancellationToken)
    {
        var renditions = await context.Renditions
            .Where(rendition => rendition.Lesson.Course.LibraryId == libraryId && rendition.Purpose == RenditionPurpose.Source)
            .ToListAsync(cancellationToken);
        var byKey = renditions.ToDictionary(rendition => (rendition.LessonId, rendition.SourceGeneration));

        foreach (var lesson in lessons)
        {
            var videoComponent = lesson.SourceComponents.FirstOrDefault(component =>
                component.Generation == lesson.SourceGeneration && component.Role == SourceComponentRole.Video);
            if (videoComponent is null)
            {
                continue;
            }

            var probedThisScan = discovered.Lessons.GetValueOrDefault(lesson.PrimaryRelativePath)?.Probe;
            var probe = probedThisScan is not null
                ? new ProbeMetadata(probedThisScan.DurationMs, probedThisScan.VideoCodec, probedThisScan.AudioCodec, probedThisScan.Width, probedThisScan.Height)
                : ParseProbeMetadata(videoComponent.ProbeMetadata);

            var containerCompatible = IsBrowserCompatibleContainer(lesson.PrimaryRelativePath);
            var status = !containerCompatible
                ? RenditionStatus.Pending
                : probe is not null
                    ? RenditionStatus.Ready
                    : RenditionStatus.Stale;

            if (!byKey.TryGetValue((lesson.Id, lesson.SourceGeneration), out var rendition))
            {
                rendition = new RenditionEntity
                {
                    LessonId = lesson.Id,
                    SourceGeneration = lesson.SourceGeneration,
                    Purpose = RenditionPurpose.Source,
                    RetentionClass = RenditionRetention.Source,
                    RelativePath = videoComponent.RelativePath
                };
                context.Renditions.Add(rendition);
                byKey[(lesson.Id, lesson.SourceGeneration)] = rendition;
            }

            if (rendition.Status == RenditionStatus.Ready && status != RenditionStatus.Ready)
            {
                rendition.Revision++;
            }

            rendition.Status = lesson.Availability == CatalogAvailability.Missing
                ? RenditionStatus.Missing
                : status;
            rendition.ByteLength = videoComponent.LengthBytes;
            rendition.Width = probe?.Width;
            rendition.Height = probe?.Height;
            rendition.VideoCodec = probe?.VideoCodec;
            rendition.AudioCodec = probe?.AudioCodec;
            rendition.RecipeVersion = null;
            rendition.Profile = null;
            if (lesson.Availability == CatalogAvailability.Missing)
            {
                rendition.Revision++;
            }
        }

        foreach (var rendition in renditions)
        {
            if (rendition.SourceGeneration == rendition.Lesson.SourceGeneration)
            {
                continue;
            }

            if (rendition.Status == RenditionStatus.Ready)
            {
                rendition.Status = RenditionStatus.Stale;
                rendition.Revision++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    internal static ProbeMetadata? ParseProbeMetadata(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProbeMetadata>(json, ProbeJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsBrowserCompatibleContainer(string relativePath)
    {
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        return extension is ".mp4" or ".m4v" or ".mkv" or ".webm";
    }

    private static string? MediaMimeTypeFor(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".ts" or ".mts" or ".m2ts" => "video/mp2t",
        _ => null
    };

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
            track.Availability = CatalogAvailability.Available;

            // The parse state belongs to a specific set of bytes. It is preserved while the
            // file still matches what was converted, and reset only when the size or
            // modification time show a different file, so a rescan neither discards a good
            // conversion nor lets an edited sidecar keep claiming it is converted.
            var matchesConverted = track.NormalizedSourceLengthBytes == subtitle.LengthBytes
                && track.NormalizedSourceModifiedUtcMs == subtitle.ModifiedUtcMs;
            if (!matchesConverted)
            {
                track.ParseStatus = "Discovered";
                track.ParseError = null;
                track.NormalizedRelativePath = null;
                track.Fingerprint = null;
                track.NormalizedSourceLengthBytes = null;
                track.NormalizedSourceModifiedUtcMs = null;
            }
            SubtitleConverter.TrySplitLanguageSuffix(
                Path.GetFileNameWithoutExtension(subtitle.RelativePath), out _, out var parsedLanguage);
            track.Language = parsedLanguage;
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

            // Deleting the row used to cascade away the learner's manual subtitle preference,
            // which SUB-03 requires to be explained rather than forgotten. The track is kept
            // and marked missing so the association survives and the reason can be shown.
            track.Availability = CatalogAvailability.Missing;
            track.NormalizedRelativePath = null;
            track.NormalizedSourceLengthBytes = null;
            track.NormalizedSourceModifiedUtcMs = null;
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

    private void AddSourceComponents(
        LessonEntity lesson,
        DiscoveredLesson discovered,
        int generation,
        SourceComponentEntity? unchangedVideo,
        SourceComponentEntity? unchangedCompanion)
    {
        context.SourceComponents.Add(new SourceComponentEntity
        {
            Lesson = lesson,
            Generation = generation,
            Role = SourceComponentRole.Video,
            RelativePath = discovered.RelativePath,
            LengthBytes = discovered.LengthBytes,
            ModifiedUtcMs = discovered.ModifiedUtcMs,
            Sha256 = discovered.Sha256 ?? unchangedVideo?.Sha256,
            ProbeMetadata = SerializeProbe(discovered.Probe) ?? unchangedVideo?.ProbeMetadata
        });

        if (discovered.CompanionRelativePath is null)
        {
            // A removed companion is simply absent from the new generation instead of
            // leaving a stale component attached to the lesson.
            return;
        }

        context.SourceComponents.Add(new SourceComponentEntity
        {
            Lesson = lesson,
            Generation = generation,
            Role = SourceComponentRole.CompanionAudio,
            RelativePath = discovered.CompanionRelativePath,
            LengthBytes = discovered.CompanionLengthBytes!.Value,
            ModifiedUtcMs = discovered.CompanionModifiedUtcMs!.Value,
            Sha256 = discovered.CompanionSha256 ?? unchangedCompanion?.Sha256
        });
    }

    private static bool VideoContentChanged(SourceComponentEntity component, DiscoveredLesson discovered)
    {
        if (discovered.Sha256 is null)
        {
            // The source was not inspected during this scan because its size, modification
            // time and stored probe metadata all matched; the identity is unchanged.
            return false;
        }

        if (component.Sha256 is not null)
        {
            // Complete content digests decide identity. Equal length, duration, codecs and
            // dimensions are not evidence that the bytes are the same bytes.
            return !string.Equals(component.Sha256, discovered.Sha256, StringComparison.Ordinal);
        }

        // No digest was ever established for the stored generation, so fall back to hints.
        // The freshly calculated digest is persisted, so the next change is decided by content.
        return component.LengthBytes != discovered.LengthBytes
            || ProbeDiffers(component, discovered.Probe);
    }

    private static bool CompanionSetChanged(SourceComponentEntity? component, DiscoveredLesson discovered)
    {
        if (component is null)
        {
            // A companion that appeared since the last scan changes the source set and has
            // to be recorded, which the previous both-non-null comparison never did.
            return discovered.CompanionRelativePath is not null;
        }

        if (discovered.CompanionRelativePath is null)
        {
            return true;
        }

        if (!string.Equals(component.RelativePath, discovered.CompanionRelativePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (discovered.CompanionSha256 is null)
        {
            return false;
        }

        return component.Sha256 is not null
            ? !string.Equals(component.Sha256, discovered.CompanionSha256, StringComparison.Ordinal)
            : component.LengthBytes != discovered.CompanionLengthBytes;
    }

    private async Task RollbackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbException or DbUpdateException)
        {
            logger.LogWarning(exception, "The scan transaction could not be rolled back cleanly.");
        }
        finally
        {
            // Everything the failed reconciliation staged is discarded so the failure is
            // recorded through a clean unit of work outside the rolled-back transaction.
            context.ChangeTracker.Clear();
        }
    }

    private async Task AbortRunAsync(long scanRunId, string state, CancellationToken cancellationToken, string? message = null)
    {
        try
        {
            context.ChangeTracker.Clear();
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
        catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException or DbException)
        {
            logger.LogError(
                exception,
                "Scan {ScanRunId} could not be recorded as {State}; it may need recovery on the next startup.",
                scanRunId, state);
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
