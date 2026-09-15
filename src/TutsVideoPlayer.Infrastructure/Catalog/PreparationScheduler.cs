using System.Globalization;
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
            if (extension is ".mp4" or ".m4v" or ".mkv" or ".webm")
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
            else if (extension is not ".wmv" and not ".ts" and not ".mts" and not ".m2ts")
            {
                var probe = LibraryScanner.ParseProbeMetadata(video.ProbeMetadata);
                if (probe?.VideoCodec != "h264")
                {
                    blocked = true;
                    errorCode = UnsupportedSourceCode;
                    message = "This source format has no supported preparation recipe in this version.";
                }
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
            context.PreparationJobs.Add(job);
            jobsByLessonGeneration[key] = job;
            created++;
        }

        foreach (var job in jobs.Where(job => job.State == PreparationJobState.Blocked
                     && job.ErrorCode == MissingCompanionCode))
        {
            var companion = lessons
                .Where(lesson => lesson.Id == job.LessonId && lesson.SourceGeneration == job.SourceGeneration)
                .SelectMany(lesson => lesson.SourceComponents)
                .Any(component => component.Generation == job.SourceGeneration
                    && component.Role == SourceComponentRole.CompanionAudio);

            if (companion)
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