using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TutsVideoPlayer.Core.Learning;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Learning;

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

        int? ifMatchRevision = ParseIfMatch();

        try
        {
            return Ok(await learning.SetCompletionAsync(lessonId, choice, ifMatchRevision, cancellationToken));
        }
        catch (InvalidOperationException exception) when (exception is not RevisionConflictException)
        {
            return NotFound();
        }
        catch (RevisionConflictException exception)
        {
            HttpContext.Response.Headers.ETag = $"\"{exception.CurrentRevision.ToString(CultureInfo.InvariantCulture)}\"";
            return Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Stale revision",
                detail: $"The completion state changed concurrently; the current revision is {exception.CurrentRevision}.");
        }
    }

    private int? ParseIfMatch()
    {
        var header = Request.Headers.IfMatch.FirstOrDefault();
        if (header is null)
        {
            return null;
        }

        var trimmed = header.Trim('"', 'W', '/', ' ');
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            ? revision
            : null;
    }
}

[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController(LearningService learning) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SettingsModel>> GetSettings(CancellationToken cancellationToken) =>
        Ok(await learning.GetSettingsAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<SettingsModel>> UpdateSettings(
        [FromBody] SettingsUpdateModel update,
        CancellationToken cancellationToken)
    {
        int? ifMatchRevision = null;
        var header = Request.Headers.IfMatch.FirstOrDefault();
        if (header is not null)
        {
            var trimmed = header.Trim('"', ' ');
            if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            {
                ifMatchRevision = revision;
            }
        }

        try
        {
            return Ok(await learning.UpdateSettingsAsync(update, ifMatchRevision, cancellationToken));
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
            HttpContext.Response.Headers.ETag = $"\"{exception.CurrentRevision.ToString(CultureInfo.InvariantCulture)}\"";
            return Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Stale revision",
                detail: $"The settings changed concurrently; the current revision is {exception.CurrentRevision}.");
        }
    }
}