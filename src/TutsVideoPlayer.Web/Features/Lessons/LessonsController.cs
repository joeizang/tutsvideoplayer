using System.Globalization;
using System.Text.Json;
using TutsVideoPlayer.Core.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Lessons;

[ApiController]
[Route("api/v1/lessons")]
public sealed class LessonsController(AppDbContext context) : ControllerBase
{
    [HttpGet("{lessonId:long}")]
    public async Task<ActionResult<LessonDetailModel>> GetLesson(long lessonId, CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons.AsNoTracking()
            .Where(candidate => candidate.Id == lessonId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.PrimaryRelativePath,
                candidate.Title,
                candidate.DurationMs,
                candidate.Availability,
                candidate.SourceGeneration,
                CourseId = candidate.Course.Id,
                CourseTitle = candidate.Course.DisplayTitle,
                Probe = candidate.SourceComponents
                    .Where(component => component.Generation == candidate.SourceGeneration && component.Role == SourceComponentRole.Video)
                    .Select(component => component.ProbeMetadata)
                    .FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (lesson is null)
        {
            return NotFound();
        }

        var neighbours = await context.Lessons.AsNoTracking()
            .Where(candidate => candidate.CourseId == lesson.CourseId)
            .OrderBy(candidate => candidate.SortKey)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new { candidate.Id, candidate.SortKey })
            .ToListAsync(cancellationToken);

        var index = neighbours.FindIndex(candidate => candidate.Id == lesson.Id);
        string? previousLessonId = index > 0 ? neighbours[index - 1].Id.ToString(CultureInfo.InvariantCulture) : null;
        string? nextLessonId = index >= 0 && index < neighbours.Count - 1
            ? neighbours[index + 1].Id.ToString(CultureInfo.InvariantCulture)
            : null;

        var subtitleCandidateCount = await context.SubtitleTracks.AsNoTracking()
            .CountAsync(track => track.CourseId == lesson.CourseId, cancellationToken);

        ProbeSummaryModel? probe = null;
        if (lesson.Probe is not null)
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<Infrastructure.Media.ProbeMetadata>(lesson.Probe);
                if (metadata is not null)
                {
                    probe = new ProbeSummaryModel(metadata.VideoCodec, metadata.AudioCodec, metadata.Width, metadata.Height);
                }
            }
            catch (JsonException)
            {
            }
        }

        return Ok(new LessonDetailModel(
            lesson.Id.ToString(CultureInfo.InvariantCulture),
            lesson.CourseId.ToString(CultureInfo.InvariantCulture),
            lesson.CourseTitle,
            lesson.Title,
            lesson.PrimaryRelativePath,
            lesson.Availability.ToString(),
            lesson.DurationMs,
            lesson.SourceGeneration,
            probe,
            subtitleCandidateCount,
            previousLessonId,
            nextLessonId));
    }
}
