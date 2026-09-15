using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Features.Preparation;

public sealed record EvictionOutcome(int EvictedCount, long ReclaimedBytes, long? PendingBytes);

/// <summary>
/// Two-phase cache eviction. Only Ready Quality renditions are ever eligible —
/// permanent copies and source files are never touched. A rendition is claimed
/// as Evicting with a revision check (so no new playback session can select it),
/// its validated managed files are removed, and it is marked Evicted.
/// </summary>
public sealed class CacheEvictionService(AppDbContext context, IOptions<AppOptions> appOptions, ILogger<CacheEvictionService> logger)
{
    public async Task<EvictionOutcome> EvictUntilUnderLimitAsync(long cacheLimitBytes, CancellationToken cancellationToken = default)
    {
        var reclaimed = 0L;
        var evicted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usage = await GetUsageAsync(cancellationToken);
            if (usage <= cacheLimitBytes)
            {
                break;
            }

            var candidate = await SelectNextCandidateAsync(cancellationToken);
            if (candidate is null)
            {
                logger.LogWarning(
                    "Cache usage {Usage} bytes exceeds the {Limit} byte limit but every eviction candidate is protected or unavailable.",
                    usage, cacheLimitBytes);
                break;
            }

            var bytes = await EvictAsync(candidate, cancellationToken);
            reclaimed += bytes;
            evicted++;
        }

        return new EvictionOutcome(evicted, reclaimed, null);
    }

    /// <summary>Reconciles interrupted evictions: finish any half-deleted eviction.</summary>
    public async Task ReconcileInterruptedEvictionsAsync(CancellationToken cancellationToken = default)
    {
        var evicting = await context.Renditions
            .Where(rendition => rendition.Status == RenditionStatus.Evicting)
            .ToListAsync(cancellationToken);

        foreach (var rendition in evicting)
        {
            await DeleteFilesAsync(rendition, cancellationToken);
            rendition.Status = RenditionStatus.Evicted;
            rendition.Revision++;
        }

        if (evicting.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Finished {Count} interrupted eviction(s) at startup.", evicting.Count);
        }

        // A quality rendition whose managed file vanished outside the app is no
        // longer servable; mark it missing so it can be prepared again.
        var ready = await context.Renditions
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && rendition.Status == RenditionStatus.Ready)
            .ToListAsync(cancellationToken);

        foreach (var rendition in ready)
        {
            if (!FileExistsWithinRoot(rendition.RelativePath))
            {
                rendition.Status = RenditionStatus.Missing;
                rendition.Revision++;
                logger.LogWarning("Quality rendition {RenditionId} file vanished; marked missing.", rendition.Id);
            }
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<long> GetUsageAsync(CancellationToken cancellationToken)
    {
        var ready = await context.Renditions.AsNoTracking()
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && rendition.Status == RenditionStatus.Ready)
            .SumAsync(rendition => rendition.ByteLength, cancellationToken);
        return ready;
    }

    private async Task<RenditionEntity?> SelectNextCandidateAsync(CancellationToken cancellationToken)
    {
        var graceCutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - CacheAccounting.LeaseGraceMs;

        var protectedIds = await context.PlaybackSessions.AsNoTracking()
            .Where(session => session.ClosedUtcMs == null
                && session.ActiveRenditionId != null
                && session.LastHeartbeatUtcMs >= graceCutoff)
            .Select(session => session.ActiveRenditionId!.Value)
            .ToListAsync(cancellationToken);

        // Least-recently-used first; nulls (never accessed) are the coldest.
        var candidates = await context.Renditions
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && rendition.Status == RenditionStatus.Ready)
            .OrderBy(rendition => rendition.LastAccessUtcMs ?? long.MinValue)
            .ThenBy(rendition => rendition.Id)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(candidate => !protectedIds.Contains(candidate.Id))
            ?? candidates.FirstOrDefault();
    }

    private async Task<long> EvictAsync(RenditionEntity rendition, CancellationToken cancellationToken)
    {
        // Two-phase: claim with a revision check so a concurrent playback-session
        // selection loses instead of serving a file that is about to vanish.
        var claimed = await context.Renditions
            .Where(candidate => candidate.Id == rendition.Id
                && candidate.Status == RenditionStatus.Ready
                && candidate.Revision == rendition.Revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, RenditionStatus.Evicting)
                .SetProperty(candidate => candidate.Revision, rendition.Revision + 1), cancellationToken);

        if (claimed == 0)
        {
            return 0;
        }

        var bytes = rendition.ByteLength;
        await DeleteFilesAsync(rendition, cancellationToken);

        await context.Renditions
            .Where(candidate => candidate.Id == rendition.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, RenditionStatus.Evicted)
                .SetProperty(candidate => candidate.Revision, rendition.Revision + 2), cancellationToken);

        logger.LogInformation("Evicted quality rendition {RenditionId} ({Bytes} bytes).", rendition.Id, bytes);
        return bytes;
    }

    private async Task DeleteFilesAsync(RenditionEntity rendition, CancellationToken cancellationToken)
    {
        foreach (var relative in new[] { rendition.RelativePath, rendition.ManifestPath })
        {
            if (relative is null)
            {
                continue;
            }

            var absolute = Path.IsPathRooted(relative)
                ? relative
                : Path.Combine(appOptions.Value.LibraryRoot, relative);
            try
            {
                if (File.Exists(absolute))
                {
                    File.Delete(absolute);
                }
            }
            catch (IOException exception)
            {
                logger.LogError(exception, "The eviction of {Path} could not delete the file.", absolute);
            }

            await Task.CompletedTask;
        }
    }

    private bool FileExistsWithinRoot(string relative)
    {
        var absolute = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(appOptions.Value.LibraryRoot, relative);
        return File.Exists(absolute);
    }
}