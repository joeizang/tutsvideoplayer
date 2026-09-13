using Microsoft.Extensions.Options;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Library;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.Web.Features.Library;

[ApiController]
[Route("api/v1/library")]
public sealed class LibraryController(
    AppDbContext context,
    ScanCoordinator coordinator,
    SchemaReadiness readiness,
    IOptions<AppOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<LibrarySummaryModel>> GetLibrary(CancellationToken cancellationToken)
    {
        var library = await context.Libraries.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (library is null)
        {
            return Ok(new LibrarySummaryModel(
                "Tutorial library",
                string.Empty,
                0,
                0, 0, 0, 0,
                null,
                BuildStatus()));
        }

        var courseCount = await context.Courses.AsNoTracking().CountAsync(cancellationToken);
        var lessonCounts = await context.Lessons.AsNoTracking()
            .GroupBy(lesson => lesson.Availability)
            .Select(group => new { Availability = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var subtitleCount = await context.SubtitleTracks.AsNoTracking().CountAsync(cancellationToken);

        ScanStatusModel? latestScan = null;
        var latest = await context.ScanRuns.AsNoTracking()
            .OrderByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null)
        {
            latestScan = ToScanStatus(latest);
        }

        return Ok(new LibrarySummaryModel(
            library.DisplayName,
            library.LogicalIdentity,
            library.CatalogRevision,
            courseCount,
            lessonCounts.FirstOrDefault(group => group.Availability == Core.Catalog.CatalogAvailability.Available)?.Count ?? 0,
            lessonCounts.FirstOrDefault(group => group.Availability == Core.Catalog.CatalogAvailability.Missing)?.Count ?? 0,
            subtitleCount,
            latestScan,
            BuildStatus()));
    }

    [HttpPost("scans")]
    public async Task<ActionResult<ScanStartResultModel>> StartScan(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.LibraryRoot))
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "No library root configured",
                detail: "Configure App:LibraryRoot on the host before scanning.");
        }

        var result = await coordinator.StartOrJoinAsync(cancellationToken);
        return Accepted(new ScanStartResultModel(
            result.ScanRunId.ToString(CultureInfo.InvariantCulture),
            result.State,
            result.Joined));
    }

    [HttpGet("scans/{scanRunId:long}")]
    public async Task<ActionResult<ScanStatusModel>> GetScan(long scanRunId, CancellationToken cancellationToken)
    {
        var run = await context.ScanRuns.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == scanRunId, cancellationToken);

        return run is null ? NotFound() : Ok(ToScanStatus(run));
    }

    [HttpGet("scans/{scanRunId:long}/issues")]
    public async Task<ActionResult<IReadOnlyList<ScanIssueModel>>> GetScanIssues(
        long scanRunId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var exists = await context.ScanRuns.AsNoTracking()
            .AnyAsync(run => run.Id == scanRunId, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }

        var issues = await context.ScanIssues.AsNoTracking()
            .Where(issue => issue.ScanRunId == scanRunId)
            .OrderBy(issue => issue.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(issue => new ScanIssueModel(
                issue.Id.ToString(CultureInfo.InvariantCulture),
                issue.RelativePath,
                issue.Code,
                issue.Message))
            .ToListAsync(cancellationToken);

        return Ok(issues);
    }

    private LibraryStatusModel BuildStatus() => new(
        readiness.IsReady,
        !string.IsNullOrWhiteSpace(options.Value.LibraryRoot),
        !string.IsNullOrWhiteSpace(options.Value.LibraryRoot) && Directory.Exists(options.Value.LibraryRoot));

    private static ScanStatusModel ToScanStatus(ScanRunEntity run) => new(
        run.Id.ToString(CultureInfo.InvariantCulture),
        run.StartedUtcMs,
        run.FinishedUtcMs,
        run.State,
        run.DiscoveredCount,
        run.IssueCount,
        run.CatalogRevision);
}
