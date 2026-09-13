using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JsxCore;
using JsxCore.Hosting;
using JsxCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Home;

public sealed class HomeController(AppDbContext context) : Controller
{
    private const int CoursePageSize = 24;

    [HttpGet("/")]
    public async Task<IActionResult> Index([FromQuery] string? q, CancellationToken cancellationToken)
    {
        var library = await context.Libraries.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        var courseCount = await context.Courses.AsNoTracking().CountAsync(cancellationToken);
        var lessonCounts = await context.Lessons.AsNoTracking()
            .GroupBy(lesson => lesson.Availability)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var subtitleCount = await context.SubtitleTracks.AsNoTracking().CountAsync(cancellationToken);
        var latest = await context.ScanRuns.AsNoTracking()
            .OrderByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var coursesQuery = context.Courses.AsNoTracking().AsQueryable();
        var searchQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (searchQuery is not null)
        {
            var pattern = $"%{Features.Courses.CoursesController.EscapeLike(searchQuery)}%";
            coursesQuery = coursesQuery.Where(course => EF.Functions.Like(course.SearchTitle, pattern, "\\")
                || course.Lessons.Any(lesson => EF.Functions.Like(lesson.SearchTitle, pattern, "\\")));
        }

        var courses = await coursesQuery
            .OrderBy(course => course.SortKey)
            .ThenBy(course => course.Id)
            .Take(CoursePageSize)
            .Select(course => new CourseSummaryModel(
                course.Id.ToString(CultureInfo.InvariantCulture),
                course.DisplayTitle,
                course.Lessons.Count(),
                course.Lessons.Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Available),
                course.Lessons.Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Missing),
                course.Availability == Core.Catalog.CatalogAvailability.Available))
            .ToListAsync(cancellationToken);

        var model = new LibraryHomeModel(
            new LibrarySummaryModel(
                library?.DisplayName ?? "Tutorial library",
                library?.LogicalIdentity ?? string.Empty,
                library?.CatalogRevision ?? 0,
                courseCount,
                lessonCounts.FirstOrDefault(group => group.Key == Core.Catalog.CatalogAvailability.Available)?.Count ?? 0,
                lessonCounts.FirstOrDefault(group => group.Key == Core.Catalog.CatalogAvailability.Missing)?.Count ?? 0,
                subtitleCount,
                latest is null
                    ? null
                    : new ScanStatusModel(
                        latest.Id.ToString(CultureInfo.InvariantCulture),
                        latest.StartedUtcMs,
                        latest.FinishedUtcMs,
                        latest.State,
                        latest.DiscoveredCount,
                        latest.IssueCount,
                        latest.CatalogRevision),
                new LibraryStatusModel(true, true, true)),
            courses,
            searchQuery);

        return this.Jsx("Home/Index", model, RenderMode.ServerAndClient);
    }
}
