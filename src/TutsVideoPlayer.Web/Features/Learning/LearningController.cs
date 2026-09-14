using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TutsVideoPlayer.Core.Learning;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Learning;

/// <summary>
/// Parsing for the If-Match precondition that settings and completion updates require.
/// A missing or malformed header is reported as such instead of quietly becoming "no
/// precondition", which would let a stale tab overwrite a newer choice.
/// </summary>
internal readonly record struct Precondition(bool Present, bool Valid, int Revision)
{
    public static Precondition Read(HttpRequest request)
    {
        var header = request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
        {
            return new Precondition(Present: false, Valid: false, Revision: 0);
        }

        var trimmed = header.Trim();
        if (trimmed.StartsWith("W/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        trimmed = trimmed.Trim('"', ' ');
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            ? new Precondition(Present: true, Valid: true, revision)
            : new Precondition(Present: true, Valid: false, Revision: 0);
    }
}

internal static class RevisionResponses
{
    public static void SetETag(HttpResponse response, int revision) =>
        response.Headers.ETag = $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    public static ProblemDetails PreconditionRequired(int currentRevision, string resource) => new()
    {
        Status = StatusCodes.Status428PreconditionRequired,
        Title = "Precondition required",
        Detail = $"This {resource} update must carry an If-Match header with the current revision, {currentRevision}."
    };

    public static ProblemDetails Concurrent(string resource) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Concurrent write",
        Detail = $"The {resource} is being written concurrently. Re-read the current revision and try again."
    };
}

[ApiController]
[Route("api/v1/learning")]
public sealed class LearningController(LearningService learning) : ControllerBase
{
    [HttpGet("continue")]
    public async Task<ActionResult<IReadOnlyList<ContinueLearningEntryModel>>> GetContinue(
        [FromQuery] int take = 5,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 20);
        return Ok(await learning.ContinueLearningAsync(take, cancellationToken));
    }
}

[ApiController]
[Route("api/v1/lessons/{lessonId:long}/completion")]
public sealed class CompletionController(LearningService learning) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CompletionResultModel>> GetCompletion(long lessonId, CancellationToken cancellationToken)
    {
        try
        {
            var current = await learning.GetCompletionAsync(lessonId, cancellationToken);
            RevisionResponses.SetETag(Response, current.Revision);
            return Ok(current);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPut]
    public async Task<ActionResult<CompletionResultModel>> SetCompletion(
        long lessonId,
        [FromBody] CompletionRequestModel request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CompletionChoice>(request.Choice, ignoreCase: true, out var choice))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid completion choice",
                detail: "Choice must be Automatic, Completed, or Incomplete.");
        }

        CompletionResultModel current;
        try
        {
            current = await learning.GetCompletionAsync(lessonId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }

        var precondition = Precondition.Read(Request);
        if (!precondition.Valid)
        {
            RevisionResponses.SetETag(Response, current.Revision);
            var problem = RevisionResponses.PreconditionRequired(current.Revision, "completion");
            return StatusCode(problem.Status!.Value, problem);
        }

        try
        {
            var result = await learning.SetCompletionAsync(lessonId, choice, precondition.Revision, cancellationToken);
            RevisionResponses.SetETag(Response, result.Revision);
            return Ok(result);
        }
        catch (RevisionConflictException exception)
        {
            RevisionResponses.SetETag(Response, exception.CurrentRevision);
            return Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Stale revision",
                detail: $"The completion state changed concurrently; the current revision is {exception.CurrentRevision}.");
        }
        catch (ConcurrentWriteException)
        {
            var problem = RevisionResponses.Concurrent("completion state");
            return StatusCode(problem.Status!.Value, problem);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}

[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController(LearningService learning) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SettingsModel>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await learning.GetSettingsAsync(cancellationToken);
        RevisionResponses.SetETag(Response, settings.Revision);
        return Ok(settings);
    }

    [HttpPut]
    public async Task<ActionResult<SettingsModel>> UpdateSettings(
        [FromBody] SettingsUpdateModel update,
        CancellationToken cancellationToken)
    {
        var current = await learning.GetSettingsAsync(cancellationToken);

        var precondition = Precondition.Read(Request);
        if (!precondition.Valid)
        {
            RevisionResponses.SetETag(Response, current.Revision);
            var problem = RevisionResponses.PreconditionRequired(current.Revision, "settings");
            return StatusCode(problem.Status!.Value, problem);
        }

        try
        {
            var result = await learning.UpdateSettingsAsync(update, precondition.Revision, cancellationToken);
            RevisionResponses.SetETag(Response, result.Revision);
            return Ok(result);
        }
        catch (ArgumentException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid setting",
                detail: exception.Message);
        }
        catch (RevisionConflictException exception)
        {
            RevisionResponses.SetETag(Response, exception.CurrentRevision);
            return Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Stale revision",
                detail: $"The settings changed concurrently; the current revision is {exception.CurrentRevision}.");
        }
        catch (ConcurrentWriteException)
        {
            var problem = RevisionResponses.Concurrent("settings");
            return StatusCode(problem.Status!.Value, problem);
        }
    }
}
