using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using JsxCore;
using JsxCore.Hosting;
using JsxCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Models;
using ZLinq;

namespace TutsVideoPlayer.Web.Features.Home;

public sealed class HomeController(AppDbContext context, LearningService learning) : Controller
{
    private const int DefaultCoursePageSize = 24;
    private const int MaximumCoursePageSize = 100;

    [HttpGet("/")]
    public async Task<IActionResult> Index(
        [FromQuery] string? q,
        [FromQuery] int page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        var effectivePageSize = Math.Clamp(pageSize ?? DefaultCoursePageSize, 1, MaximumCoursePageSize);

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

        var coursesQuery = context.Courses.AsNoTracking();
        var searchQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (searchQuery is not null)
        {
            var pattern = $"%{Features.Courses.CoursesController.EscapeLike(searchQuery)}%";
            coursesQuery = coursesQuery.Where(course => EF.Functions.Like(course.SearchTitle, pattern, "\\")
                || course.Lessons.Any(lesson => EF.Functions.Like(lesson.SearchTitle, pattern, "\\")));
        }

        // LIB-07 requires the course list to stay complete as folders are added, so the
        // page size is a page boundary rather than a silent cap on what can be browsed.
        var matchingCourseCount = await coursesQuery.CountAsync(cancellationToken);
        var lastPage = Math.Max(1, (int)Math.Ceiling(matchingCourseCount / (double)effectivePageSize));
        page = Math.Min(page, lastPage);

        // var coursesQuery = context.Courses.AsNoTracking().AsQueryable();
        // var searchQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (searchQuery is not null)
        {
            var pattern = $"%{Features.Courses.CoursesController.EscapeLike(searchQuery)}%";
            coursesQuery = coursesQuery.Where(course => EF.Functions.Like(course.SearchTitle, pattern, "\\")
                || course.Lessons.Any(lesson => EF.Functions.Like(lesson.SearchTitle, pattern, "\\")));
        }

        // LIB-07 requires the course list to stay complete as folders are added, so the
        // page size is a page boundary rather than a silent cap on what can be browsed.
        // var matchingCourseCount = await coursesQuery.CountAsync(cancellationToken);
        // var lastPage = Math.Max(1, (int)Math.Ceiling(matchingCourseCount / (double)effectivePageSize));
        page = Math.Min(page, lastPage);

        var courseItems = await coursesQuery.Include(course => course.Lessons)
            .OrderBy(course => course.SortKey)
            .ThenBy(course => course.Id)
            .Skip((page - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var allLessonIds = courseItems.AsValueEnumerable().SelectMany(course => course.Lessons).Select(lesson => lesson.Id).Distinct().ToList();
        var lessonProgress = await context.LessonProgress.AsNoTracking()
            .Where(progress => allLessonIds.Contains(progress.LessonId))
            .Select(progress => new
            {
                progress.LessonId,
                progress.SourceGeneration,
                Completed = progress.ManualCompletion == TutsVideoPlayer.Core.Learning.CompletionChoice.Completed
                    || (progress.ManualCompletion == null && progress.AutomaticCompleted)
            })
            .ToListAsync(cancellationToken);

        var courses = courseItems.AsValueEnumerable().Select(course => new CourseSummaryModel(
            course.Id.ToString(CultureInfo.InvariantCulture),
            course.DisplayTitle,
            course.Lessons.AsValueEnumerable().Count(),
            course.Lessons.AsValueEnumerable().Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Available),
            course.Lessons.AsValueEnumerable().Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Missing),
            course.Lessons.AsValueEnumerable().Count(lesson => lesson.Availability == Core.Catalog.CatalogAvailability.Available
                && lessonProgress.Any(progress => progress.LessonId == lesson.Id
                    && progress.SourceGeneration == lesson.SourceGeneration
                    && progress.Completed)),
            course.Availability == Core.Catalog.CatalogAvailability.Available))
            .ToList();

        var continueEntries = await learning.ContinueLearningAsync(5, cancellationToken);
        var queue = await BuildQueueSummaryAsync(cancellationToken);

        var model = new LibraryHomeModel(
            new LibrarySummaryModel(
                library?.DisplayName ?? "Tutorial library",
                library?.LogicalIdentity ?? string.Empty,
                library?.CatalogRevision ?? 0,
                courseCount,
                lessonCounts.AsValueEnumerable().FirstOrDefault(group => group.Key == Core.Catalog.CatalogAvailability.Available)?.Count ?? 0,
                lessonCounts.AsValueEnumerable().FirstOrDefault(group => group.Key == Core.Catalog.CatalogAvailability.Missing)?.Count ?? 0,
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
            continueEntries,
            queue,
            searchQuery,
            page,
            effectivePageSize,
            matchingCourseCount);

        return this.Jsx("Home/Index", model, RenderMode.ServerAndClient);
    }

    private async Task<QueueSummaryModel> BuildQueueSummaryAsync(CancellationToken cancellationToken)
    {
        var preferences = await context.Preferences.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var counts = await context.PreparationJobs.AsNoTracking()
            .GroupBy(job => job.State)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return new QueueSummaryModel(
            preferences?.QueuePaused ?? false,
            preferences?.Revision ?? 0,
            counts.FirstOrDefault(group => group.Key == PreparationJobState.Running)?.Count ?? 0,
            counts.FirstOrDefault(group => group.Key == PreparationJobState.Queued)?.Count ?? 0,
            counts.FirstOrDefault(group => group.Key == PreparationJobState.Failed)?.Count ?? 0,
            counts.FirstOrDefault(group => group.Key == PreparationJobState.Blocked)?.Count ?? 0);
    }
}
