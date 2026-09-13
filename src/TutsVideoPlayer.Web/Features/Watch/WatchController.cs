using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using TutsVideoPlayer.Core.Catalog;
using JsxCore;
using JsxCore.Hosting;
using JsxCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Watch;

public sealed class WatchController(AppDbContext context) : Controller
{
    [HttpGet("/watch/{lessonId:long}")]
    public async Task<IActionResult> Lesson(long lessonId, CancellationToken cancellationToken)
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
                    .Where(component => component.Generation == candidate.SourceGeneration
                                        && component.Role == SourceComponentRole.Video)
                    .Select(component => component.ProbeMetadata)
                    .FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (lesson is null)
        {
            return Redirect("/");
        }

        var course = await context.Courses.AsNoTracking()
            .SingleAsync(course => course.Id == lesson.CourseId, cancellationToken);

        var folders = await context.LessonFolders.AsNoTracking()
            .Where(folder => folder.CourseId == lesson.CourseId)
            .Select(folder => new CourseTreeNodeModel(
                folder.Id.ToString(CultureInfo.InvariantCulture),
                "folder",
                folder.Title,
                folder.SortKey,
                folder.ParentFolderId.HasValue ? folder.ParentFolderId.Value.ToString(CultureInfo.InvariantCulture) : null,
                null,
                null,
                null))
            .ToListAsync(cancellationToken);

        var lessons = await context.Lessons.AsNoTracking()
            .Where(candidate => candidate.CourseId == lesson.CourseId)
            .OrderBy(candidate => candidate.SortKey)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new CourseTreeNodeModel(
                candidate.Id.ToString(CultureInfo.InvariantCulture),
                "lesson",
                candidate.Title,
                candidate.SortKey,
                candidate.FolderId.HasValue ? candidate.FolderId.Value.ToString(CultureInfo.InvariantCulture) : null,
                candidate.Availability == Core.Catalog.CatalogAvailability.Available,
                candidate.PrimaryRelativePath,
                candidate.DurationMs))
            .ToListAsync(cancellationToken);

        var subtitleCandidateCount = await context.SubtitleTracks.AsNoTracking()
            .CountAsync(track => track.CourseId == lesson.CourseId, cancellationToken);

        var neighbours = lessons.Select(node => node.Id).ToList();
        var index = neighbours.IndexOf(lesson.Id.ToString(CultureInfo.InvariantCulture));

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

        var model = new WatchLessonModel(
            new LessonDetailModel(
                lesson.Id.ToString(CultureInfo.InvariantCulture),
                lesson.CourseId.ToString(CultureInfo.InvariantCulture),
                course.DisplayTitle,
                lesson.Title,
                lesson.PrimaryRelativePath,
                lesson.Availability.ToString(),
                lesson.DurationMs,
                lesson.SourceGeneration,
                probe,
                subtitleCandidateCount,
                index > 0 ? neighbours[index - 1] : null,
                index >= 0 && index < neighbours.Count - 1 ? neighbours[index + 1] : null),
            new CourseTreeModel(
                course.Id.ToString(CultureInfo.InvariantCulture),
                course.DisplayTitle,
                course.Availability == Core.Catalog.CatalogAvailability.Available,
                folders.Concat(lessons)
                    .OrderBy(node => node.SortKey, StringComparer.Ordinal)
                    .ThenBy(node => node.Type, StringComparer.Ordinal)
                    .ToList()));

        return this.Jsx("Watch/Lesson", model, RenderMode.ServerAndClient);
    }
}
