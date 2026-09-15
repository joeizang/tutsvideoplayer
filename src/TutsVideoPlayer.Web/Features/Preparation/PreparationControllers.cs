using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Preparation;

[ApiController]
[Route("api/v1/lessons/{lessonId:long}/preparations")]
public sealed class LessonPreparationsController(AppDbContext context) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<PreparationJobModel>> Create(
        long lessonId,
        [FromBody] PreparationRequestModel? request,
        CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == lessonId, cancellationToken);
        if (lesson is null)
        {
            return NotFound();
        }

        var purpose = request?.Purpose is null or "Compatibility" or "compatibility"
            ? RenditionPurpose.Compatibility
            : RenditionPurpose.Quality;
        if (purpose == RenditionPurpose.Quality)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Quality preparation not yet available",
                detail: "Quality versions arrive in milestone 5; compatibility preparation is available now.");
        }

        var dedupKey = PreparationScheduler.DedupKeyFor(lessonId, lesson.SourceGeneration);
        var existing = await context.PreparationJobs.AsNoTracking()
            .SingleOrDefaultAsync(job => job.DedupKey == dedupKey, cancellationToken);
        if (existing is not null)
        {
            return Accepted(ToModel(existing, lesson.Title));
        }

        var job = new PreparationJobEntity
        {
            LessonId = lessonId,
            SourceGeneration = lesson.SourceGeneration,
            DedupKey = dedupKey,
            Purpose = RenditionPurpose.Compatibility,
            RecipeVersion = "explicit-request",
            State = PreparationJobState.Queued,
            Priority = 5,
            EnqueuedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        job = await PreparationJobStore.GetOrCreateAsync(context, job, cancellationToken);

        return Accepted(ToModel(job, lesson.Title));
    }

    internal static PreparationJobModel ToModel(PreparationJobEntity job, string lessonTitle) => new(
        job.Id.ToString(CultureInfo.InvariantCulture),
        job.LessonId.ToString(CultureInfo.InvariantCulture),
        lessonTitle,
        job.Purpose.ToString(),
        job.State.ToString(),
        job.Progress,
        job.Attempt,
        job.ErrorCode,
        job.UserMessage,
        job.Priority,
        job.EnqueuedUtcMs);
}

[ApiController]
[Route("api/v1/preparations")]
public sealed class PreparationsController(AppDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PreparationJobModel>>> List(
        [FromQuery] string? state,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        var jobs = context.PreparationJobs.AsNoTracking()
            .Include(job => job.Lesson)
            .OrderByDescending(job => job.State == PreparationJobState.Running)
            .ThenByDescending(job => job.Priority)
            .ThenBy(job => job.State == PreparationJobState.Queued ? 0 : 1)
            .ThenBy(job => job.EnqueuedUtcMs)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(state)
            && Enum.TryParse<PreparationJobState>(state, ignoreCase: true, out var parsed))
        {
            jobs = jobs.Where(job => job.State == parsed);
        }

        var models = await jobs
            .Take(limit)
            .Select(job => new
            {
                Job = job,
                job.Lesson.Title
            })
            .ToListAsync(cancellationToken);

        return Ok(models.Select(entry => LessonPreparationsController.ToModel(entry.Job, entry.Title)).ToList());
    }

    [HttpPost("{jobId:long}/retry")]
    public async Task<IActionResult> Retry(long jobId, CancellationToken cancellationToken)
    {
        var job = await context.PreparationJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        if (job.State is not (PreparationJobState.Failed or PreparationJobState.Blocked or PreparationJobState.Interrupted))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job cannot be retried",
                detail: $"A {job.State} job cannot be retried; it is already queued, running, or complete.");
        }

        job.State = PreparationJobState.Queued;
        job.Attempt = 0;
        job.Progress = null;
        job.ErrorCode = null;
        job.UserMessage = null;
        job.LeaseOwner = null;
        job.LeaseExpiresUtcMs = null;
        job.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        return Accepted(new { jobId = job.Id.ToString(CultureInfo.InvariantCulture), state = job.State.ToString() });
    }

    [HttpPost("{jobId:long}/prioritize")]
    public async Task<IActionResult> Prioritize(long jobId, CancellationToken cancellationToken)
    {
        var job = await context.PreparationJobs
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        if (job.State is not (PreparationJobState.Queued or PreparationJobState.Blocked))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job cannot be prioritized",
                detail: "Only queued work can be prioritized; a running encode is never interrupted.");
        }

        job.Priority = Math.Max(job.Priority, 10);
        job.Revision++;
        await context.SaveChangesAsync(cancellationToken);
        return Ok(new { jobId = job.Id.ToString(CultureInfo.InvariantCulture), priority = job.Priority });
    }

    [HttpPut("queue")]
    public async Task<IActionResult> SetQueuePaused(
        [FromBody] QueuePauseModel request,
        CancellationToken cancellationToken)
    {
        var precondition = Precondition.Read(Request);

        var preferences = await context.Preferences.SingleOrDefaultAsync(cancellationToken);
        if (preferences is null)
        {
            preferences = new PreferenceEntity();
            context.Preferences.Add(preferences);
            await context.SaveChangesAsync(cancellationToken);
        }

        if (!precondition.Present)
        {
            RevisionResponses.SetETag(Response, preferences.Revision);
            return StatusCode(StatusCodes.Status428PreconditionRequired, RevisionResponses.PreconditionRequired(preferences.Revision, "queue pause"));
        }

        if (!precondition.Valid || precondition.Revision != preferences.Revision)
        {
            RevisionResponses.SetETag(Response, preferences.Revision);
            return StatusCode(StatusCodes.Status412PreconditionFailed, RevisionResponses.Concurrent("queue pause"));
        }

        preferences.QueuePaused = request.Paused;
        preferences.Revision++;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            await context.Entry(preferences).ReloadAsync(cancellationToken);
            RevisionResponses.SetETag(Response, preferences.Revision);
            return StatusCode(StatusCodes.Status412PreconditionFailed, RevisionResponses.Concurrent("queue pause"));
        }

        RevisionResponses.SetETag(Response, preferences.Revision);
        return Ok(new { paused = preferences.QueuePaused, revision = preferences.Revision });
    }

    [HttpGet("queue")]
    public async Task<IActionResult> GetQueueState(CancellationToken cancellationToken)
    {
        var preferences = await context.Preferences.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var running = await context.PreparationJobs.AsNoTracking()
            .CountAsync(job => job.State == PreparationJobState.Running, cancellationToken);
        var queued = await context.PreparationJobs.AsNoTracking()
            .CountAsync(job => job.State == PreparationJobState.Queued, cancellationToken);
        var failed = await context.PreparationJobs.AsNoTracking()
            .CountAsync(job => job.State == PreparationJobState.Failed, cancellationToken);
        var blocked = await context.PreparationJobs.AsNoTracking()
            .CountAsync(job => job.State == PreparationJobState.Blocked, cancellationToken);

        return Ok(new
        {
            paused = preferences?.QueuePaused ?? false,
            revision = preferences?.Revision ?? 0,
            running,
            queued,
            failed,
            blocked
        });
    }
}