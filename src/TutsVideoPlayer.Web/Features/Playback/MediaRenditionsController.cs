using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Features.Playback;

[ApiController]
[Route("media/renditions")]
public sealed class MediaRenditionsController(AppDbContext context, IOptions<AppOptions> options) : ControllerBase
{
    [HttpGet("{renditionId:long}")]
    [HttpHead("{renditionId:long}")]
    public async Task<IActionResult> Get(long renditionId, CancellationToken cancellationToken)
    {
        var rendition = await context.Renditions.AsNoTracking()
            .Where(candidate => candidate.Id == renditionId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Status,
                candidate.RelativePath,
                candidate.ByteLength,
                LessonAvailable = candidate.Lesson.Availability == CatalogAvailability.Available
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (rendition is null)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Rendition not found",
                detail: "The requested rendition does not exist in the catalog.");
        }

        if (!rendition.LessonAvailable)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Lesson unavailable",
                detail: "The lesson for this rendition is currently unavailable.");
        }

        if (rendition.Status != RenditionStatus.Ready)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Rendition not ready",
                detail: $"This rendition is {rendition.Status.ToString()?.ToLowerInvariant()} and cannot be played yet.");
        }

        if (!LibraryPathGuard.TryResolveWithin(options.Value.LibraryRoot, rendition.RelativePath, out var fullPath))
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Media file unavailable",
                detail: "The media file could not be located inside the library root.");
        }

        var file = new FileInfo(fullPath);
        return PhysicalFile(
            fullPath,
            MediaMimeType(fullPath),
            file.LastWriteTimeUtc,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{rendition.Id.ToString(CultureInfo.InvariantCulture)}-{rendition.ByteLength.ToString(CultureInfo.InvariantCulture)}\""),
            enableRangeProcessing: true);
    }

    private static string MediaMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };
}