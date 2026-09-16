using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Core.Learning;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Models;
using TutsVideoPlayer.Web.Features.Preparation;
using TutsVideoPlayer.Web.Features.Subtitles;

namespace TutsVideoPlayer.Web.Features.Learning;

public sealed class LearningService(AppDbContext context, Subtitles.SubtitleService subtitles)
{
    public const int HeartbeatIntervalMs = 10_000;
    private const double CompletionThreshold = 0.95;

    /// <summary>
    /// Revision is an EF concurrency token, so two overlapping writes can both pass their
    /// in-memory checks and only collide at SaveChanges. Rather than surfacing that as a 500,
    /// the unit of work is reloaded and re-evaluated: the loser then sees the winner's state
    /// and produces the documented answer for it — a duplicate acknowledgement, a stale-write
    /// conflict, or a revision precondition failure.
    /// </summary>
    private const int MaxConcurrencyAttempts = 4;

    private async Task<T> WithConcurrencyRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string description,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Each attempt re-reads through a clean change tracker so the retry evaluates the
            // committed state rather than the stale entities the failed attempt loaded.
            context.ChangeTracker.Clear();

            try
            {
                return await operation(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsRetryableConflict(exception) && attempt < MaxConcurrencyAttempts)
            {
            }
            catch (DbUpdateException exception) when (IsRetryableConflict(exception))
            {
                throw new ConcurrentWriteException(description, exception);
            }
        }
    }

    private static bool IsRetryableConflict(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException
        || (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 }
            && exception.Entries.Count > 0
            && exception.Entries.All(entry => entry.Entity is LessonProgressEntity && entry.State == EntityState.Added));

    public async Task<PlaybackManifestModel> BuildManifestAsync(long lessonId, CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons.AsNoTracking()
            .Where(candidate => candidate.Id == lessonId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.SourceGeneration,
                candidate.DurationMs,
                candidate.Availability
            })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        var progress = await context.LessonProgress.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.LessonId == lessonId && candidate.SourceGeneration == lesson.SourceGeneration, cancellationToken);

        var renditions = await context.Renditions.AsNoTracking()
            .Where(rendition => rendition.LessonId == lessonId && rendition.SourceGeneration == lesson.SourceGeneration)
            .OrderByDescending(rendition => rendition.Height ?? 0)
            .ToListAsync(cancellationToken);

        var activeJob = await context.PreparationJobs.AsNoTracking()
            .Where(job => job.LessonId == lessonId
                && job.SourceGeneration == lesson.SourceGeneration
                && (job.State == PreparationJobState.Queued
                    || job.State == PreparationJobState.Running
                    || job.State == PreparationJobState.Validating
                    || job.State == PreparationJobState.Publishing
                    || job.State == PreparationJobState.Blocked
                    || job.State == PreparationJobState.Interrupted
                    || job.State == PreparationJobState.Failed))
            .OrderByDescending(job => job.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var preferences = await EnsurePreferencesAsync(cancellationToken);
        var subtitleCandidates = await subtitles.GetCandidatesAsync(lessonId, cancellationToken);
        // The resolved track and its visibility are separate facts. Blanking the selection
        // while subtitles are globally off meant that turning them back on left the video
        // captionless despite a saved preference or an exact automatic match.
        var selectedSubtitleId = subtitleCandidates.ResolvedId;

        var renditionModels = renditions
            .Select(rendition => new RenditionModel(
                rendition.Id.ToString(CultureInfo.InvariantCulture),
                RenditionLabel(rendition),
                rendition.Width,
                rendition.Height,
                MediaMimeType(rendition.RelativePath),
                rendition.VideoCodec is null ? null : $"{rendition.VideoCodec}{(rendition.AudioCodec is null ? "" : $" + {rendition.AudioCodec}")}",
                rendition.Status.ToString(),
                rendition.RetentionClass.ToString(),
                rendition.Status == RenditionStatus.Ready
                    ? $"/media/renditions/{rendition.Id.ToString(CultureInfo.InvariantCulture)}"
                    : null,
                rendition.Profile))
            .ToList();

        if (activeJob is not null)
        {
            renditionModels.Add(new RenditionModel(
                $"job-{activeJob.Id.ToString(CultureInfo.InvariantCulture)}",
                "Playback copy (preparing)",
                null,
                null,
                "application/octet-stream",
                null,
                activeJob.State.ToString(),
                "Permanent",
                null,
                activeJob.Profile,
                activeJob.Id.ToString(CultureInfo.InvariantCulture),
                activeJob.Progress,
                activeJob.UserMessage));
        }

        var readyDefault = SelectDefaultRendition(renditions, preferences.PreferredQuality);

        // Explicit lesson opening: when the preferred quality is eligible but every
        // ready rendition is taller (or the only playable rendition is the taller
        // original), queue the preferred version instead of silently degrading.
        if (lesson.Availability == CatalogAvailability.Available)
        {
            await QualityScheduling.TryQueuePreferredQualityAsync(context, lessonId, preferences.PreferredQuality, cancellationToken);
        }

        var effective = progress is null
            ? false
            : CompletionResolver.IsEffectivelyComplete(progress.AutomaticCompleted, progress.ManualCompletion);


        return new PlaybackManifestModel(
            lesson.Id.ToString(CultureInfo.InvariantCulture),
            lesson.SourceGeneration,
            lesson.DurationMs,
            progress?.Revision ?? 0,
            new ProgressStateModel(
                progress?.PositionMs ?? 0,
                effective,
                progress?.ManualCompletion?.ToString(),
                progress?.Revision ?? 0),
            renditionModels,
            subtitleCandidates.Candidates.Select(candidate => new ManifestSubtitleModel(
                candidate.Id,
                candidate.Label,
                candidate.Language,
                candidate.State,
                candidate.Reason,
                candidate.TrackUrl,
                candidate.Message)).ToList(),
            new SubtitleSelectionStateModel(selectedSubtitleId, subtitleCandidates.SubtitlesEnabled),
            readyDefault is null ? null : readyDefault.Id.ToString(CultureInfo.InvariantCulture),
            new PlaybackPreferencesModel(preferences.PlaybackSpeed, preferences.Autoplay, preferences.FitMode, preferences.Revision));
    }

    public Task<PlaybackSessionStartModel> StartSessionAsync(long lessonId, CancellationToken cancellationToken) =>
        WithConcurrencyRetryAsync(token => StartSessionCoreAsync(lessonId, token), "playback session start", cancellationToken);

    private async Task<PlaybackSessionStartModel> StartSessionCoreAsync(long lessonId, CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons
            .Include(candidate => candidate.SourceComponents)
            .SingleOrDefaultAsync(candidate => candidate.Id == lessonId, cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        var progress = await GetOrStartProgressAsync(lesson, cancellationToken);

        var session = new PlaybackSessionEntity
        {
            LessonId = lesson.Id,
            SourceGeneration = lesson.SourceGeneration,
            StartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastHeartbeatUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        context.PlaybackSessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);

        progress.ActiveSessionId = session.Id;
        progress.Revision++;
        await TouchSourceAccessAsync(lesson.Id, lesson.SourceGeneration, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return new PlaybackSessionStartModel(
            session.Id.ToString(CultureInfo.InvariantCulture),
            progress.Revision,
            progress.PositionMs,
            progress.LastSequence,
            HeartbeatIntervalMs);
    }

    public Task<HeartbeatResultModel> HeartbeatAsync(long sessionId, long? activeRenditionId, CancellationToken cancellationToken) =>
        WithConcurrencyRetryAsync(token => HeartbeatCoreAsync(sessionId, activeRenditionId, token), "heartbeat", cancellationToken);

    private async Task<HeartbeatResultModel> HeartbeatCoreAsync(long sessionId, long? activeRenditionId, CancellationToken cancellationToken)
    {
        var session = await context.PlaybackSessions
            .SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Session not found.");

        if (session.ClosedUtcMs is not null)
        {
            throw new InvalidOperationException("Session is closed.");
        }

        session.LastHeartbeatUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (activeRenditionId.HasValue && session.ActiveRenditionId != activeRenditionId.Value)
        {
            session.ActiveRenditionId = activeRenditionId.Value;
            var rendition = await context.Renditions
                .SingleOrDefaultAsync(candidate => candidate.Id == activeRenditionId.Value, cancellationToken);
            if (rendition is not null)
            {
                rendition.LastAccessUtcMs = session.LastHeartbeatUtcMs;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return new HeartbeatResultModel(true, session.LastHeartbeatUtcMs);
    }

    public sealed record ProgressOutcome(long AcceptedPositionMs, bool EffectiveCompletion, int Revision, bool Duplicate);

    public Task<ProgressOutcome> WriteProgressAsync(
        long sessionId,
        ProgressWriteModel command,
        CancellationToken cancellationToken) =>
        WithConcurrencyRetryAsync(token => WriteProgressCoreAsync(sessionId, command, token), "progress write", cancellationToken);

    private async Task<ProgressOutcome> WriteProgressCoreAsync(
        long sessionId,
        ProgressWriteModel command,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionWithLessonAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Session not found.");

        var lesson = session.Lesson;
        ValidateSessionUsable(session, lesson.SourceGeneration, command.SourceGeneration);

        var progress = await context.LessonProgress
            .SingleAsync(candidate => candidate.LessonId == lesson.Id && candidate.SourceGeneration == lesson.SourceGeneration, cancellationToken);

        if (progress.ActiveSessionId != sessionId)
        {
            throw new SessionOwnershipException("Another playback session owns this lesson's progress.");
        }

        if (command.Sequence == progress.LastSequence)
        {
            return new ProgressOutcome(progress.PositionMs, EffectiveCompletion(progress), progress.Revision, Duplicate: true);
        }

        if (command.Sequence < progress.LastSequence)
        {
            throw new SessionOwnershipException("A newer progress write already superseded this sequence.");
        }

        var clamped = ClampPosition(command.PositionMs, lesson.DurationMs);
        progress.PositionMs = clamped;
        progress.MaxObservedPositionMs = Math.Max(progress.MaxObservedPositionMs, clamped);
        progress.LastSequence = command.Sequence;
        progress.LastWatchedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        progress.Revision++;

        if (command.Ended
            || (command.IsPlaying && lesson.DurationMs is > 0 && clamped >= (long)(lesson.DurationMs.Value * CompletionThreshold)))
        {
            progress.AutomaticCompleted = true;
        }

        await context.SaveChangesAsync(cancellationToken);
        return new ProgressOutcome(progress.PositionMs, EffectiveCompletion(progress), progress.Revision, Duplicate: false);
    }

    public sealed record CloseOutcome(bool Closed, bool ProgressFlushed);

    public Task<CloseOutcome> CloseSessionAsync(
        long sessionId,
        SessionCloseModel? final,
        CancellationToken cancellationToken) =>
        WithConcurrencyRetryAsync(token => CloseSessionCoreAsync(sessionId, final, token), "session close", cancellationToken);

    private async Task<CloseOutcome> CloseSessionCoreAsync(
        long sessionId,
        SessionCloseModel? final,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionWithLessonAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("Session not found.");

        if (session.ClosedUtcMs is not null)
        {
            return new CloseOutcome(Closed: true, ProgressFlushed: false);
        }

        var lesson = session.Lesson;
        var progress = await context.LessonProgress
            .SingleOrDefaultAsync(candidate => candidate.LessonId == lesson.Id && candidate.SourceGeneration == lesson.SourceGeneration, cancellationToken);

        var flushed = false;
        if (progress is not null
            && progress.ActiveSessionId == sessionId
            && final?.Sequence is not null
            && final.PositionMs is not null
            && final.Sequence.Value > progress.LastSequence)
        {
            progress.PositionMs = ClampPosition(final.PositionMs.Value, lesson.DurationMs);
            progress.MaxObservedPositionMs = Math.Max(progress.MaxObservedPositionMs, progress.PositionMs);
            progress.LastSequence = final.Sequence.Value;
            progress.LastWatchedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            progress.Revision++;
            flushed = true;
        }

        if (progress is not null && progress.ActiveSessionId == sessionId)
        {
            progress.ActiveSessionId = null;
            progress.Revision++;
        }

        session.ClosedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await context.SaveChangesAsync(cancellationToken);
        return new CloseOutcome(Closed: true, ProgressFlushed: flushed);
    }

    public Task<CompletionResultModel> SetCompletionAsync(
        long lessonId,
        CompletionChoice choice,
        int ifMatchRevision,
        CancellationToken cancellationToken) =>
        WithConcurrencyRetryAsync(token => SetCompletionCoreAsync(lessonId, choice, ifMatchRevision, token), "completion update", cancellationToken);

    /// <summary>
    /// Current completion state without changing anything, so a request that arrives without a
    /// usable precondition can be answered with the revision it needs to retry.
    /// </summary>
    public async Task<CompletionResultModel> GetCompletionAsync(long lessonId, CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons.AsNoTracking()
            .Where(candidate => candidate.Id == lessonId)
            .Select(candidate => new { candidate.Id, candidate.SourceGeneration })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        var progress = await context.LessonProgress.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.LessonId == lesson.Id && candidate.SourceGeneration == lesson.SourceGeneration,
                cancellationToken);

        return new CompletionResultModel(
            progress?.ManualCompletion?.ToString() ?? CompletionChoice.Automatic.ToString(),
            progress is not null && CompletionResolver.IsEffectivelyComplete(progress.AutomaticCompleted, progress.ManualCompletion),
            progress?.Revision ?? 0);
    }

    private async Task<CompletionResultModel> SetCompletionCoreAsync(
        long lessonId,
        CompletionChoice choice,
        int ifMatchRevision,
        CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons
            .SingleOrDefaultAsync(candidate => candidate.Id == lessonId, cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        var progress = await GetOrStartProgressAsync(lesson, cancellationToken);

        if (ifMatchRevision != progress.Revision)
        {
            throw new RevisionConflictException(progress.Revision);
        }

        progress.ManualCompletion = choice switch
        {
            CompletionChoice.Automatic => null,
            CompletionChoice.Completed => CompletionChoice.Completed,
            CompletionChoice.Incomplete => CompletionChoice.Incomplete,
            _ => progress.ManualCompletion
        };
        progress.Revision++;
        await context.SaveChangesAsync(cancellationToken);

        return new CompletionResultModel(
            choice.ToString(),
            EffectiveCompletion(progress),
            progress.Revision);
    }

    public async Task<IReadOnlyList<ContinueLearningEntryModel>> ContinueLearningAsync(int take, CancellationToken cancellationToken)
    {
        // Only the current source generation describes the video the reader would open now.
        // Rows from a replaced source keep their history, but advertising their position or
        // completion here would resume into content that no longer exists.
        var rows = await context.LessonProgress.AsNoTracking()
            .Where(candidate => candidate.LastWatchedUtcMs > 0
                && candidate.SourceGeneration == candidate.Lesson.SourceGeneration)
            .OrderByDescending(candidate => candidate.LastWatchedUtcMs)
            .Select(candidate => new
            {
                candidate.LessonId,
                candidate.PositionMs,
                candidate.AutomaticCompleted,
                candidate.ManualCompletion,
                LastWatchedUtcMs = candidate.LastWatchedUtcMs,
                LessonTitle = candidate.Lesson.Title,
                LessonSortKey = candidate.Lesson.SortKey,
                LessonDurationMs = candidate.Lesson.DurationMs,
                LessonAvailable = candidate.Lesson.Availability == CatalogAvailability.Available,
                CourseId = candidate.Lesson.Course.Id,
                CourseTitle = candidate.Lesson.Course.DisplayTitle
            })
            .ToListAsync(cancellationToken);

        // Superseded rows are not used for recommendations, but their existence is worth
        // saying out loud so a reader understands why a familiar lesson restarts at zero.
        var lessonIdsWithHistory = await context.LessonProgress.AsNoTracking()
            .Where(candidate => candidate.SourceGeneration != candidate.Lesson.SourceGeneration)
            .Select(candidate => candidate.LessonId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var historical = lessonIdsWithHistory.ToHashSet();

        var continueEntries = new List<ContinueLearningEntryModel>();
        var seenCourses = new HashSet<long>();
        var courseIds = rows.Select(row => row.CourseId).Distinct().ToList();
        var courseLessons = await context.Lessons.AsNoTracking()
            .Where(lesson => courseIds.Contains(lesson.CourseId) && lesson.Availability == CatalogAvailability.Available)
            .OrderBy(lesson => lesson.SortKey)
            .ThenBy(lesson => lesson.Id)
            .Select(lesson => new
            {
                lesson.Id,
                lesson.CourseId,
                Completed = context.LessonProgress.Any(progress =>
                    progress.LessonId == lesson.Id
                    && progress.SourceGeneration == lesson.SourceGeneration
                    && (progress.ManualCompletion == CompletionChoice.Completed
                        || (progress.ManualCompletion == null && progress.AutomaticCompleted)))
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (!seenCourses.Add(row.CourseId))
            {
                continue;
            }

            var lessons = courseLessons.Where(lesson => lesson.CourseId == row.CourseId).ToList();
            var effectivelyComplete = CompletionResolver.IsEffectivelyComplete(row.AutomaticCompleted, row.ManualCompletion);

            string recommendedLessonId;
            string recommendation;
            if (lessons.Count == 0)
            {
                recommendedLessonId = row.LessonId.ToString(CultureInfo.InvariantCulture);
                recommendation = "unavailable";
            }
            else if (effectivelyComplete)
            {
                var next = lessons.FirstOrDefault(lesson => !lesson.Completed);
                if (next is null)
                {
                    recommendedLessonId = lessons.FirstOrDefault()?.Id.ToString(CultureInfo.InvariantCulture) ?? row.LessonId.ToString(CultureInfo.InvariantCulture);
                    recommendation = "replay";
                }
                else
                {
                    recommendedLessonId = next.Id.ToString(CultureInfo.InvariantCulture);
                    recommendation = "next";
                }
            }
            else if (row.LessonAvailable)
            {
                recommendedLessonId = row.LessonId.ToString(CultureInfo.InvariantCulture);
                recommendation = "resume";
            }
            else
            {
                // The remembered lesson's source has gone missing. Its history is kept, but
                // Resume must not point at a page that cannot play; the next unfinished
                // available lesson in the course is a usable recommendation instead.
                var fallback = lessons.FirstOrDefault(lesson => !lesson.Completed) ?? lessons.FirstOrDefault();
                if (fallback is null)
                {
                    recommendedLessonId = row.LessonId.ToString(CultureInfo.InvariantCulture);
                    recommendation = "unavailable";
                }
                else
                {
                    recommendedLessonId = fallback.Id.ToString(CultureInfo.InvariantCulture);
                    recommendation = "next";
                }
            }

            continueEntries.Add(new ContinueLearningEntryModel(
                row.CourseId.ToString(CultureInfo.InvariantCulture),
                row.CourseTitle,
                row.LessonId.ToString(CultureInfo.InvariantCulture),
                row.LessonTitle,
                row.PositionMs,
                row.LessonDurationMs,
                lessons.Count(lesson => lesson.Completed),
                lessons.Count,
                recommendedLessonId,
                recommendation,
                row.LessonAvailable,
                historical.Contains(row.LessonId)));

            if (continueEntries.Count >= take)
            {
                break;
            }
        }

        return continueEntries;
    }

    public async Task<SettingsModel> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var preferences = await EnsurePreferencesAsync(cancellationToken);
        return new SettingsModel(
            preferences.PlaybackSpeed,
            preferences.Autoplay,
            preferences.FitMode,
            preferences.SubtitleEnabled,
            preferences.PreferredQuality,
            preferences.CacheLimitBytes,
            preferences.Revision);
    }

    public Task<SettingsModel> UpdateSettingsAsync(
        SettingsUpdateModel update,
        int ifMatchRevision,
        CancellationToken cancellationToken) =>
        WithConcurrencyRetryAsync(token => UpdateSettingsCoreAsync(update, ifMatchRevision, token), "settings update", cancellationToken);

    private async Task<SettingsModel> UpdateSettingsCoreAsync(
        SettingsUpdateModel update,
        int ifMatchRevision,
        CancellationToken cancellationToken)
    {
        if (update.PlaybackSpeed.HasValue && !PlaybackSpeeds.IsAllowed(update.PlaybackSpeed.Value))
        {
            throw new ArgumentException("Playback speed must be one of 0.5, 0.75, 1, 1.25, 1.5, 1.75, or 2.");
        }

        if (update.FitMode is not null && update.FitMode is not ("Contain" or "Fill"))
        {
            throw new ArgumentException("Fit mode must be Contain or Fill.");
        }

        var preferences = await EnsurePreferencesAsync(cancellationToken);
        if (ifMatchRevision != preferences.Revision)
        {
            throw new RevisionConflictException(preferences.Revision);
        }

        if (update.PlaybackSpeed.HasValue)
        {
            preferences.PlaybackSpeed = update.PlaybackSpeed.Value;
        }

        if (update.Autoplay.HasValue)
        {
            preferences.Autoplay = update.Autoplay.Value;
        }

        if (update.FitMode is not null)
        {
            preferences.FitMode = update.FitMode;
        }

        if (update.SubtitleEnabled.HasValue)
        {
            preferences.SubtitleEnabled = update.SubtitleEnabled.Value;
        }

        if (update.PreferredQuality is not null)
        {
            if (update.PreferredQuality.Length > 0 && !QualityProfiles.IsKnown(update.PreferredQuality))
            {
                throw new ArgumentException("Preferred quality must be 1080, 720, or 480.");
            }

            preferences.PreferredQuality = update.PreferredQuality.Length == 0 ? null : update.PreferredQuality.ToLowerInvariant();
        }

        if (update.CacheLimitBytes.HasValue)
        {
            if (update.CacheLimitBytes.Value < CacheAccounting.MinimumCacheLimitBytes)
            {
                throw new ArgumentException($"The cache limit must be at least {CacheAccounting.MinimumCacheLimitBytes} bytes.");
            }

            preferences.CacheLimitBytes = update.CacheLimitBytes.Value;
        }

        preferences.Revision++;
        await context.SaveChangesAsync(cancellationToken);

        return new SettingsModel(
            preferences.PlaybackSpeed,
            preferences.Autoplay,
            preferences.FitMode,
            preferences.SubtitleEnabled,
            preferences.PreferredQuality,
            preferences.CacheLimitBytes,
            preferences.Revision);
    }

    private async Task<LessonProgressEntity> GetOrStartProgressAsync(LessonEntity lesson, CancellationToken cancellationToken)
    {
        var progress = await context.LessonProgress
            .SingleOrDefaultAsync(candidate => candidate.LessonId == lesson.Id && candidate.SourceGeneration == lesson.SourceGeneration, cancellationToken);

        if (progress is null)
        {
            progress = new LessonProgressEntity
            {
                LessonId = lesson.Id,
                SourceGeneration = lesson.SourceGeneration
            };
            context.LessonProgress.Add(progress);
            await context.SaveChangesAsync(cancellationToken);
        }

        return progress;
    }

    private async Task<PreferenceEntity> EnsurePreferencesAsync(CancellationToken cancellationToken)
    {
        var preferences = await context.Preferences.SingleOrDefaultAsync(cancellationToken);
        if (preferences is null)
        {
            preferences = new PreferenceEntity();
            context.Preferences.Add(preferences);
            await context.SaveChangesAsync(cancellationToken);
        }

        return preferences;
    }

    private async Task<PlaybackSessionEntity?> LoadSessionWithLessonAsync(long sessionId, CancellationToken cancellationToken) =>
        await context.PlaybackSessions
            .Include(session => session.Lesson)
            .SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

    private static void ValidateSessionUsable(PlaybackSessionEntity session, int lessonGeneration, int commandGeneration)
    {
        if (session.ClosedUtcMs is not null)
        {
            throw new SessionOwnershipException("This playback session is closed.");
        }

        if (lessonGeneration != commandGeneration)
        {
            throw new SessionOwnershipException("The lesson source changed since this session started.");
        }
    }

    private static long ClampPosition(long positionMs, long? durationMs)
    {
        var clamped = Math.Max(0, positionMs);
        if (durationMs is > 0)
        {
            clamped = Math.Min(clamped, durationMs.Value);
        }

        return clamped;
    }

    private static bool EffectiveCompletion(LessonProgressEntity progress) =>
        CompletionResolver.IsEffectivelyComplete(progress.AutomaticCompleted, progress.ManualCompletion);

    private async Task TouchSourceAccessAsync(long lessonId, int sourceGeneration, CancellationToken cancellationToken)
    {
        var renditions = await context.Renditions
            .Where(rendition => rendition.LessonId == lessonId && rendition.SourceGeneration == sourceGeneration)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var rendition in renditions)
        {
            rendition.LastAccessUtcMs = now;
        }
    }

    private static string RenditionLabel(RenditionEntity rendition) =>
        rendition.Purpose switch
        {
            RenditionPurpose.Source => QualityProfiles.LabelFor(null, rendition.Width, rendition.Height),
            RenditionPurpose.Compatibility => "Playback copy",
            RenditionPurpose.Quality => rendition.Profile is null
                ? "Quality version"
                : QualityProfiles.LabelFor(rendition.Profile, rendition.Width, rendition.Height),
            _ => "Rendition"
        };

    /// <summary>
    /// PRD 0003 default selection: the preferred profile's ready rendition wins;
    /// otherwise the highest ready rendition at or below the preferred height;
    /// otherwise (only taller playable renditions) the tallest ready rendition
    /// plays immediately while the preferred version is queued.
    /// </summary>
    private static RenditionEntity? SelectDefaultRendition(
        IReadOnlyList<RenditionEntity> renditions,
        string? preferredQuality)
    {
        var ready = renditions.Where(rendition => rendition.Status == RenditionStatus.Ready).ToList();
        if (ready.Count == 0)
        {
            return null;
        }

        static int OrderKey(RenditionEntity rendition) => rendition.Height ?? 0;

        if (preferredQuality is not null)
        {
            var preferredMatch = ready.FirstOrDefault(rendition =>
                string.Equals(rendition.Profile, preferredQuality, StringComparison.OrdinalIgnoreCase));
            if (preferredMatch is not null)
            {
                return preferredMatch;
            }

            var preferredHeight = QualityProfiles.TargetHeight(preferredQuality);
            if (preferredHeight is not null)
            {
                var atOrBelow = ready
                    .Where(rendition => (rendition.Height ?? 0) <= preferredHeight.Value)
                    .OrderByDescending(OrderKey)
                    .ThenBy(rendition => rendition.RetentionClass == RenditionRetention.Permanent ? 0 : 1)
                    .FirstOrDefault();
                if (atOrBelow is not null)
                {
                    return atOrBelow;
                }
            }
        }

        // No preferred fallback applies: prefer at-or-below 1080, else the tallest.
        return ready
            .OrderByDescending(rendition => (rendition.Height ?? 0) > 1080 ? -1 : OrderKey(rendition))
            .ThenBy(rendition => rendition.RetentionClass == RenditionRetention.Permanent ? 0 : 1)
            .First();
    }

    private static string MediaMimeType(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };
}

public sealed class SessionOwnershipException(string message) : InvalidOperationException(message);

/// <summary>
/// Raised when a unit of work kept losing an optimistic-concurrency race. Callers translate
/// it into a retryable conflict rather than an unhandled server error.
/// </summary>
public sealed class ConcurrentWriteException(string description, Exception inner)
    : InvalidOperationException($"The {description} could not be applied because the record kept changing concurrently.", inner);

public sealed class RevisionConflictException(int currentRevision)
    : InvalidOperationException($"The resource changed concurrently; current revision is {currentRevision}.")
{
    public int CurrentRevision { get; } = currentRevision;
}