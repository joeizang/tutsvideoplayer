using System.Globalization;
using TutsVideoPlayer.Infrastructure.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Core.Subtitles;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Catalog;

/// <summary>
/// Idempotent compatibility-job scheduling. Runs after every successful scan
/// (and may run at any time) so a crash between discovery and scheduling fills
/// its own gap. MP4/MKV are exempt from automatic scheduling; a missing
/// companion audio blocks the job until the prerequisite appears.
/// </summary>
public static class PreparationScheduler
{
    public const string MissingCompanionCode = "missing-companion-audio";
    public const string UnsupportedSourceCode = "unsupported-source";

    public static async Task ScheduleCompatibilityJobsAsync(
        AppDbContext context,
        long libraryId,
        string root,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var lessons = await context.Lessons
            .Include(lesson => lesson.SourceComponents)
            .Where(lesson => lesson.Course.LibraryId == libraryId && lesson.Availability == CatalogAvailability.Available)
            .ToListAsync(cancellationToken);

        var jobs = await context.PreparationJobs
            .Where(job => job.Lesson.Course.LibraryId == libraryId)
            .ToListAsync(cancellationToken);
        var jobsByLessonGeneration = jobs.ToDictionary(job => (job.LessonId, job.SourceGeneration));

        var permanentRenditions = await context.Renditions
            .Where(rendition => rendition.Lesson.Course.LibraryId == libraryId
                && rendition.Purpose == RenditionPurpose.Compatibility
                && rendition.RetentionClass == RenditionRetention.Permanent)
            .ToListAsync(cancellationToken);
        foreach (var rendition in permanentRenditions.Where(r => r.Status == RenditionStatus.Ready))
        {
            if (PreparationArtifacts.IsOwnedOutput(root, rendition.RelativePath)) continue;
            rendition.Status = RenditionStatus.Stale;
            rendition.Revision++;
            var job = jobs.FirstOrDefault(j => j.LessonId == rendition.LessonId && j.SourceGeneration == rendition.SourceGeneration && j.State == PreparationJobState.Succeeded);
            if (job is not null)
            {
                job.State = PreparationJobState.Queued;
                job.Attempt = 0;
                job.Progress = null;
                job.Revision++;
            }
        }
        await context.SaveChangesAsync(cancellationToken);
        var validRenditionsByLesson = permanentRenditions
            .Where(rendition => rendition.Status == RenditionStatus.Ready)
            .GroupBy(rendition => (rendition.LessonId, rendition.SourceGeneration))
            .ToDictionary(group => group.Key, group => group.First());

        var created = 0;
        var unblocked = 0;

        foreach (var lesson in lessons)
        {
            var key = (lesson.Id, lesson.SourceGeneration);

            if (jobsByLessonGeneration.ContainsKey(key) || validRenditionsByLesson.ContainsKey(key))
            {
                continue;
            }

            var video = lesson.SourceComponents.FirstOrDefault(component =>
                component.Generation == lesson.SourceGeneration && component.Role == SourceComponentRole.Video);
            var companion = lesson.SourceComponents.FirstOrDefault(component =>
                component.Generation == lesson.SourceGeneration && component.Role == SourceComponentRole.CompanionAudio);

            if (video is null)
            {
                continue;
            }

            var extension = Path.GetExtension(video.RelativePath).ToLowerInvariant();
            if (extension is ".mp4" or ".mkv")
            {
                continue;
            }

            var blocked = false;
            string? errorCode = null;
            string? message = null;

            if (extension is ".ts" or ".mts" or ".m2ts" && companion is null)
            {
                var probe = LibraryScanner.ParseProbeMetadata(video.ProbeMetadata);
                if (probe?.AudioCodec is null)
                {
                    blocked = true;
                    errorCode = MissingCompanionCode;
                    message = "This MPEG-TS lesson has no companion audio file yet; preparation is blocked until it appears.";
                }
            }
            if (!blocked && PreparationRecipes.Choose(new SourceSetInfo(video.RelativePath, video.RelativePath,
                    companion?.RelativePath, companion?.RelativePath, LibraryScanner.ParseProbeMetadata(video.ProbeMetadata)), out _) is null)
            {
                blocked = true;
                errorCode = UnsupportedSourceCode;
                message = "This source format has no supported preparation recipe in this version.";
            }

            var job = new PreparationJobEntity
            {
                LessonId = lesson.Id,
                SourceGeneration = lesson.SourceGeneration,
                DedupKey = DedupKeyFor(lesson.Id, lesson.SourceGeneration),
                Purpose = RenditionPurpose.Compatibility,
                RecipeVersion = RecipeVersionFor(extension),
                State = blocked ? PreparationJobState.Blocked : PreparationJobState.Queued,
                Priority = 0,
                EnqueuedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ErrorCode = errorCode,
                UserMessage = message
            };
            jobsByLessonGeneration[key] = await PreparationJobStore.GetOrCreateAsync(context, job, cancellationToken);
            created++;
        }

        foreach (var job in jobs.Where(job => job.State == PreparationJobState.Blocked
                     && (job.ErrorCode == MissingCompanionCode || job.ErrorCode == UnsupportedSourceCode)))
        {
            var companion = lessons
                .Where(lesson => lesson.Id == job.LessonId && lesson.SourceGeneration == job.SourceGeneration)
                .SelectMany(lesson => lesson.SourceComponents)
                .Any(component => component.Generation == job.SourceGeneration
                    && component.Role == SourceComponentRole.CompanionAudio);

            var lesson = lessons.FirstOrDefault(l => l.Id == job.LessonId && l.SourceGeneration == job.SourceGeneration);
            var video = lesson?.SourceComponents.FirstOrDefault(c => c.Generation == job.SourceGeneration && c.Role == SourceComponentRole.Video);
            var audio = lesson?.SourceComponents.FirstOrDefault(c => c.Generation == job.SourceGeneration && c.Role == SourceComponentRole.CompanionAudio);
            var repairedProbe = video is not null && PreparationRecipes.Choose(new SourceSetInfo(video.RelativePath, video.RelativePath,
                audio?.RelativePath, audio?.RelativePath, LibraryScanner.ParseProbeMetadata(video.ProbeMetadata)), out _) is not null;
            if (job.ErrorCode == MissingCompanionCode ? companion : repairedProbe)
            {
                job.State = PreparationJobState.Queued;
                job.ErrorCode = null;
                job.UserMessage = null;
                job.Revision++;
                unblocked++;
            }
        }

        if (created > 0 || unblocked > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Preparation scheduling created {Created} job(s) and unblocked {Unblocked}.",
                created, unblocked);
        }
    }

    public static string DedupKeyFor(long lessonId, int sourceGeneration) =>
        $"{lessonId.ToString(CultureInfo.InvariantCulture)}:{sourceGeneration.ToString(CultureInfo.InvariantCulture)}:compatibility";

    public static string RecipeVersionFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".wmv" => PreparationRecipes.WmvVersion,
        ".ts" or ".mts" or ".m2ts" => PreparationRecipes.TsAacVersion,
        _ => PreparationRecipes.RemuxVersion
    };
}

public static class QualityScheduling
{
    public sealed record QualityQueueOutcome(bool Created, bool AlreadyReady, string? BlockedReason, long? JobId);

    /// <summary>
    /// Explicit quality request for one lesson: validates eligibility (no
    /// upscaling, no duplicate of the native rendition), deduplicates against
    /// active jobs, and treats Evicted/Missing renditions as re-preparable.
    /// </summary>
    public static async Task<QualityQueueOutcome> QueueQualityForLessonAsync(
        AppDbContext context,
        long lessonId,
        string profile,
        CancellationToken cancellationToken = default)
    {
        if (!QualityProfiles.IsKnown(profile))
        {
            return new QualityQueueOutcome(false, false, "unknown-profile", null);
        }

        var lesson = await context.Lessons
            .Include(candidate => candidate.SourceComponents)
            .SingleOrDefaultAsync(candidate => candidate.Id == lessonId, cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        if (lesson.Availability != CatalogAvailability.Available)
        {
            return new QualityQueueOutcome(false, false, "lesson-unavailable", null);
        }

        var video = lesson.SourceComponents.FirstOrDefault(component =>
            component.Generation == lesson.SourceGeneration && component.Role == SourceComponentRole.Video);
        if (video is null)
        {
            return new QualityQueueOutcome(false, false, "lesson-unavailable", null);
        }

        var probe = LibraryScanner.ParseProbeMetadata(video.ProbeMetadata);
        if (probe?.Width is not > 0 || probe?.Height is not > 0)
        {
            return new QualityQueueOutcome(false, false, "probe-incomplete", null);
        }

        var eligible = QualityProfiles.EligibleProfiles(probe.Width, probe.Height);
        if (!eligible.Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            // A source whose native height equals the profile is served by its native rendition.
            return new QualityQueueOutcome(false, false, "not-eligible", null);
        }

        // An existing READY rendition with this profile means the work is done.
        var ready = await context.Renditions.AsNoTracking()
            .AnyAsync(candidate => candidate.LessonId == lessonId
                && candidate.SourceGeneration == lesson.SourceGeneration
                && candidate.Purpose == RenditionPurpose.Quality
                && candidate.Profile == profile
                && candidate.Status == RenditionStatus.Ready, cancellationToken);
        if (ready)
        {
            return new QualityQueueOutcome(false, true, null, null);
        }

        var dedupKey = PreparationScheduler.DedupKeyFor(lessonId, lesson.SourceGeneration) + $":quality:{profile.ToLowerInvariant()}";

        var existing = await context.PreparationJobs
            .SingleOrDefaultAsync(job => job.DedupKey == dedupKey, cancellationToken);
        if (existing is not null && existing.State is PreparationJobState.Queued
            or PreparationJobState.Running or PreparationJobState.Validating
            or PreparationJobState.Publishing or PreparationJobState.Blocked)
        {
            // Active work for this profile already exists; converge on it.
            return new QualityQueueOutcome(false, false, null, existing.Id);
        }

        if (existing is not null)
        {
            // A terminal job (Succeeded but its rendition was evicted, Failed, or
            // Canceled) is requeued rather than duplicated; the rendition state
            // was already checked above, so this re-preparation is warranted.
            existing.State = PreparationJobState.Queued;
            existing.ErrorCode = null;
            existing.UserMessage = null;
            existing.Revision++;
            await context.SaveChangesAsync(cancellationToken);
            return new QualityQueueOutcome(true, false, null, existing.Id);
        }

        var job = await PreparationJobStore.GetOrCreateAsync(context, new PreparationJobEntity
        {
            LessonId = lessonId,
            SourceGeneration = lesson.SourceGeneration,
            DedupKey = dedupKey,
            Purpose = RenditionPurpose.Quality,
            Profile = profile.ToLowerInvariant(),
            RecipeVersion = QualityProfiles.RecipeVersionFor(profile),
            State = PreparationJobState.Queued,
            Priority = 5,
            EnqueuedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, cancellationToken);
        return new QualityQueueOutcome(true, false, null, job.Id);
    }

    /// <summary>
    /// Explicit lesson opening: when only a higher-resolution playable source
    /// exists and the preferred quality is eligible but absent, queue it.
    /// </summary>
    public static async Task TryQueuePreferredQualityAsync(
        AppDbContext context,
        long lessonId,
        string? preferredQuality,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preferredQuality) || !QualityProfiles.IsKnown(preferredQuality))
        {
            return;
        }

        var lesson = await context.Lessons.AsNoTracking()
            .Include(candidate => candidate.SourceComponents)
            .SingleOrDefaultAsync(candidate => candidate.Id == lessonId, cancellationToken);
        if (lesson is null || lesson.Availability != CatalogAvailability.Available)
        {
            return;
        }

        var target = QualityProfiles.TargetHeight(preferredQuality);
        var video = lesson.SourceComponents.FirstOrDefault(component =>
            component.Generation == lesson.SourceGeneration && component.Role == SourceComponentRole.Video);
        var probe = video is null ? null : LibraryScanner.ParseProbeMetadata(video.ProbeMetadata);
        if (target is null || probe?.Height is not > 0)
        {
            return;
        }

        // Only queue when the source is taller than the preference and every
        // ready rendition is taller too (otherwise the fallback already serves it).
        var renditions = await context.Renditions.AsNoTracking()
            .Where(candidate => candidate.LessonId == lesson.Id
                && candidate.SourceGeneration == lesson.SourceGeneration
                && candidate.Status == RenditionStatus.Ready)
            .ToListAsync(cancellationToken);
        if (renditions.Any(rendition => (rendition.Height ?? int.MaxValue) <= target.Value))
        {
            return;
        }

        if (probe.Height.Value <= target.Value)
        {
            return;
        }

        var activeJob = await context.PreparationJobs.AsNoTracking()
            .AnyAsync(job => job.LessonId == lesson.Id
                && job.SourceGeneration == lesson.SourceGeneration
                && job.Purpose == RenditionPurpose.Quality
                && job.Profile == preferredQuality.ToLowerInvariant()
                && (job.State == PreparationJobState.Queued
                    || job.State == PreparationJobState.Running
                    || job.State == PreparationJobState.Validating
                    || job.State == PreparationJobState.Publishing
                    || job.State == PreparationJobState.Blocked), cancellationToken);
        if (activeJob)
        {
            return;
        }

        await QueueQualityForLessonAsync(context, lesson.Id, preferredQuality, cancellationToken);
    }
}
