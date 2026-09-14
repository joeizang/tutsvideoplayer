using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Playback;

[ApiController]
[Route("api/v1/lessons/{lessonId:long}")]
public sealed class PlaybackController(LearningService learning) : ControllerBase
{
    [HttpGet("playback")]
    public async Task<ActionResult<PlaybackManifestModel>> GetManifest(long lessonId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await learning.BuildManifestAsync(lessonId, cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPost("playback-sessions")]
    public async Task<ActionResult<PlaybackSessionStartModel>> StartSession(long lessonId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await learning.StartSessionAsync(lessonId, cancellationToken);
            return Created($"/api/v1/lessons/{lessonId}/playback", result);
        }
        catch (ConcurrentWriteException exception)
        {
            return Conflict(ProblemDetailsFor("concurrent-write", exception.Message));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    private static ProblemDetails ProblemDetailsFor(string code, string detail) => new()
    {
        Title = code,
        Detail = detail,
        Status = StatusCodes.Status409Conflict
    };
}

[ApiController]
[Route("api/v1/playback-sessions/{sessionId:long}")]
public sealed class PlaybackSessionController(LearningService learning) : ControllerBase
{
    [HttpPost("heartbeat")]
    public async Task<ActionResult<HeartbeatResultModel>> Heartbeat(
        long sessionId,
        [FromBody] HeartbeatModel? body,
        CancellationToken cancellationToken)
    {
        long? renditionId = null;
        if (body?.ActiveRenditionId is not null && long.TryParse(body.ActiveRenditionId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            renditionId = parsed;
        }

        try
        {
            return Ok(await learning.HeartbeatAsync(sessionId, renditionId, cancellationToken));
        }
        catch (ConcurrentWriteException exception)
        {
            return Conflict(ProblemDetailsFor("concurrent-write", exception.Message));
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("closed", StringComparison.Ordinal))
        {
            return Conflict(ProblemDetailsFor("session-closed", "This playback session is closed."));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPut("progress")]
    public async Task<ActionResult<ProgressWriteResultModel>> WriteProgress(
        long sessionId,
        [FromBody] ProgressWriteModel command,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await learning.WriteProgressAsync(sessionId, command, cancellationToken));
        }
        catch (SessionOwnershipException exception)
        {
            return Conflict(ProblemDetailsFor("stale-session", exception.Message));
        }
        catch (ConcurrentWriteException exception)
        {
            // A write that kept losing the optimistic-concurrency race is retryable, not a
            // server fault: the client repeats it with the same sequence and is acknowledged.
            return Conflict(ProblemDetailsFor("concurrent-write", exception.Message));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPost("close")]
    public async Task<ActionResult<SessionCloseResultModel>> Close(
        long sessionId,
        [FromBody] SessionCloseModel? body,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await learning.CloseSessionAsync(sessionId, body, cancellationToken));
        }
        catch (ConcurrentWriteException exception)
        {
            return Conflict(ProblemDetailsFor("concurrent-write", exception.Message));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    private static ProblemDetails ProblemDetailsFor(string code, string detail) => new()
    {
        Title = code,
        Detail = detail,
        Status = StatusCodes.Status409Conflict
    };
}