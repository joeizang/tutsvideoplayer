using Microsoft.AspNetCore.Mvc;
using TutsVideoPlayer.Infrastructure.FileSystem;

namespace TutsVideoPlayer.Web.Features.Playback;

[ApiController]
[Route("media/demo")]
public sealed class DemoMediaController(MediaFileLocator mediaFileLocator) : ControllerBase
{
    [HttpGet("{fileName}")]
    [HttpHead("{fileName}")]
    public IActionResult Get(string fileName)
    {
        if (!mediaFileLocator.TryResolve(fileName, out var fullPath))
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Media not found",
                detail: "The requested media file is not available.");
        }

        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".vtt" => "text/vtt",
            _ => "application/octet-stream"
        };

        return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
    }
}
