using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence;

namespace TutsVideoPlayer.Web.Features.Courses;

/// <summary>
/// Course entry point for browsing. Course IDs and lesson IDs are independent, so a course
/// row cannot link straight to <c>/watch/{lessonId}</c>; this route performs the lookup and
/// sends the reader to a lesson that actually belongs to the selected course.
/// </summary>
public sealed class CourseEntryController(AppDbContext context) : Controller
{
    [HttpGet("/courses/{courseId:long}")]
    public async Task<IActionResult> Enter(long courseId, CancellationToken cancellationToken)
    {
        var firstLessonId = await context.Lessons.AsNoTracking()
            .Where(lesson => lesson.CourseId == courseId)
            // Prefer a playable lesson, but still open a course whose lessons are all missing
            // so the reader can see why, in the same natural order the rail uses.
            .OrderBy(lesson => lesson.Availability == CatalogAvailability.Available ? 0 : 1)
            .ThenBy(lesson => lesson.SortKey)
            .ThenBy(lesson => lesson.Id)
            .Select(lesson => (long?)lesson.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return firstLessonId is null
            ? Redirect("/")
            : Redirect($"/watch/{firstLessonId.Value.ToString(CultureInfo.InvariantCulture)}");
    }
}
