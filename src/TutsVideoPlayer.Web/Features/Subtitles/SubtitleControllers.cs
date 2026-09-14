using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Subtitles;

namespace TutsVideoPlayer.Web.Features.Subtitles;

[ApiController]
[Route("api/v1/lessons/{lessonId:long}/subtitle-candidates")]
public sealed class SubtitleCandidatesController(SubtitleService subtitles) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SubtitleCandidatesModel>> GetCandidates(long lessonId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await subtitles.GetCandidatesAsync(lessonId, cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}

[ApiController]
[Route("api/v1/lessons/{lessonId:long}/subtitle-selection")]
public sealed class SubtitleSelectionController(SubtitleService subtitles) : ControllerBase
{
    public sealed record SelectionRequest(string? SubtitleTrackId);

    [HttpPut]
    public async Task<ActionResult<SubtitleSelectionModel>> PutSelection(
        long lessonId,
        [FromBody] SelectionRequest request,
        CancellationToken cancellationToken)
    {
        long? trackId = null;
        if (request.SubtitleTrackId is not null)
        {
            if (!long.TryParse(request.SubtitleTrackId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid subtitle track id",
                    detail: "SubtitleTrackId must be an existing track id or null for automatic selection.");
            }

            trackId = parsed;
        }

        try
        {
            return Ok(await subtitles.SelectAsync(lessonId, trackId, cancellationToken));
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("different course", StringComparison.Ordinal))
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Subtitle outside the course",
                detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Subtitle selection unavailable",
                detail: exception.Message);
        }
    }
}

[ApiController]
[Route("media/subtitles")]
public sealed class SubtitleMediaController(SubtitleService subtitles) : ControllerBase
{
    [HttpGet("{trackId:long}.vtt")]
    public async Task<IActionResult> Get(long trackId, CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await subtitles.GetDeliveryAsync(trackId, cancellationToken);
            var lastModified = new FileInfo(delivery.FullPath).LastWriteTimeUtc;
            return PhysicalFile(
                delivery.FullPath,
                "text/vtt; charset=utf-8",
                lastModified,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{delivery.Fingerprint}\""),
                enableRangeProcessing: false);
        }
        catch (SubtitleFormatException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Subtitle could not be converted",
                detail: exception.Message);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}