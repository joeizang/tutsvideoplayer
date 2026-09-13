using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Courses;

[ApiController]
[Route("api/v1/courses")]
public sealed class CoursesController(AppDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CourseListModel>> GetCourses(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var courses = context.Courses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{EscapeLike(q.Trim())}%";
            courses = courses.Where(course => EF.Functions.Like(course.SearchTitle, pattern, "\\")
                || course.Lessons.Any(lesson => EF.Functions.Like(lesson.SearchTitle, pattern, "\\")));
        }

        var totalCount = await courses.CountAsync(cancellationToken);

        var items = await courses
            .OrderBy(course => course.SortKey)
            .ThenBy(course => course.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(course => new CourseSummaryModel(
                course.Id.ToString(CultureInfo.InvariantCulture),
                course.DisplayTitle,
                course.Lessons.Count(),
                course.Lessons.Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Available),
                course.Lessons.Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Missing),
                course.Lessons.Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Available
                    && context.LessonProgress.Any(progress => progress.LessonId == lesson.Id
                        && progress.SourceGeneration == lesson.SourceGeneration
                        && (progress.ManualCompletion == TutsVideoPlayer.Core.Learning.CompletionChoice.Completed
                            || (progress.ManualCompletion == null && progress.AutomaticCompleted)))),
                course.Availability == Core.Catalog.CatalogAvailability.Available))
            .ToListAsync(cancellationToken);

        return Ok(new CourseListModel(items, page, pageSize, totalCount, string.IsNullOrWhiteSpace(q) ? null : q.Trim()));
    }

    [HttpGet("{courseId:long}/tree")]
    public async Task<ActionResult<CourseTreeModel>> GetCourseTree(long courseId, CancellationToken cancellationToken)
    {
        var course = await context.Courses.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == courseId, cancellationToken);
        if (course is null)
        {
            return NotFound();
        }

        var folders = await context.LessonFolders.AsNoTracking()
            .Where(folder => folder.CourseId == courseId)
            .Select(folder => new CourseTreeNodeModel(
                folder.Id.ToString(CultureInfo.InvariantCulture),
                "folder",
                folder.Title,
                folder.SortKey,
                folder.ParentFolderId.HasValue ? folder.ParentFolderId.Value.ToString(CultureInfo.InvariantCulture) : null,
                null,
                null,
                null,
                null))
            .ToListAsync(cancellationToken);

        var lessons = await context.Lessons.AsNoTracking()
            .Where(lesson => lesson.CourseId == courseId)
            .OrderBy(lesson => lesson.SortKey)
            .ThenBy(lesson => lesson.Id)
            .Select(lesson => new CourseTreeNodeModel(
                lesson.Id.ToString(CultureInfo.InvariantCulture),
                "lesson",
                lesson.Title,
                lesson.SortKey,
                lesson.FolderId.HasValue ? lesson.FolderId.Value.ToString(CultureInfo.InvariantCulture) : null,
                lesson.Availability == Core.Catalog.CatalogAvailability.Available,
                context.LessonProgress.Any(progress => progress.LessonId == lesson.Id
                    && progress.SourceGeneration == lesson.SourceGeneration
                    && (progress.ManualCompletion == TutsVideoPlayer.Core.Learning.CompletionChoice.Completed
                        || (progress.ManualCompletion == null && progress.AutomaticCompleted))),
                lesson.PrimaryRelativePath,
                lesson.DurationMs))
            .ToListAsync(cancellationToken);

        var nodes = folders.Concat(lessons)
            .OrderBy(node => node.SortKey, StringComparer.Ordinal)
            .ThenBy(node => node.Type, StringComparer.Ordinal)
            .ToList();

        return Ok(new CourseTreeModel(
            courseId.ToString(CultureInfo.InvariantCulture),
            course.DisplayTitle,
            course.Availability == Core.Catalog.CatalogAvailability.Available,
            nodes));
    }

    internal static string EscapeLike(string input) =>
        input.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
