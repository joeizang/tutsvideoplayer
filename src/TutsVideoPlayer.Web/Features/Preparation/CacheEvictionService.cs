using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Features.Preparation;

public sealed record EvictionOutcome(int EvictedCount, long ReclaimedBytes, long? PendingBytes);

/// <summary>
/// Two-phase cache eviction. Only Quality renditions are ever eligible — permanent copies
/// and source files are never touched. A rendition is claimed as Evicting with a revision
/// check (so no new playback session can select it), its validated managed files are
/// removed, and it is marked Evicted.
/// </summary>
public sealed class CacheEvictionService(AppDbContext context, IOptions<AppOptions> appOptions, ILogger<CacheEvictionService> logger)
{
    /// <summary>
    /// Everything a quality rendition can be while its bytes are still on disk. Stale copies
    /// (superseded by a source change) are charged and evicted too: dropping them from the
    /// accounting made them invisible to the budget and ineligible for cleanup, so repeated
    /// source replacements grew the directory without bound.
    /// </summary>
    public static bool IsChargeable(RenditionStatus status) =>
        status is RenditionStatus.Ready or RenditionStatus.Stale;

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
                return new EvictionOutcome(evicted, reclaimed, null);
            }

            var candidate = await SelectNextCandidateAsync(cancellationToken);
            if (candidate is null)
            {
                // Everything left is being watched right now. Deleting a leased rendition
                // would interrupt the video that is playing, so the shortfall is reported
                // and the caller decides whether to defer or refuse the work.
                var pending = usage - cacheLimitBytes;
                logger.LogWarning(
                    "Cache usage {Usage} bytes exceeds the {Limit} byte limit by {Pending} bytes, but every remaining quality copy is in use by a live playback session.",
                    usage, cacheLimitBytes, pending);
                return new EvictionOutcome(evicted, reclaimed, pending);
            }

            var bytes = await EvictAsync(candidate, cancellationToken);
            reclaimed += bytes;
            evicted++;
        }
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
        var live = await context.Renditions
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && (rendition.Status == RenditionStatus.Ready || rendition.Status == RenditionStatus.Stale))
            .ToListAsync(cancellationToken);

        foreach (var rendition in live)
        {
            if (!TryResolveManagedFile(rendition.RelativePath, out _))
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

    private async Task<long> GetUsageAsync(CancellationToken cancellationToken) =>
        await context.Renditions.AsNoTracking()
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && (rendition.Status == RenditionStatus.Ready || rendition.Status == RenditionStatus.Stale))
            .SumAsync(rendition => rendition.ByteLength, cancellationToken);

    private async Task<RenditionEntity?> SelectNextCandidateAsync(CancellationToken cancellationToken)
    {
        var graceCutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - CacheAccounting.LeaseGraceMs;

        var protectedIds = await context.PlaybackSessions.AsNoTracking()
            .Where(session => session.ClosedUtcMs == null
                && session.ActiveRenditionId != null
                && session.LastHeartbeatUtcMs >= graceCutoff)
            .Select(session => session.ActiveRenditionId!.Value)
            .ToListAsync(cancellationToken);

        // Stale copies first — they can no longer be played at all, so reclaiming them
        // costs nothing. Then least-recently-used; nulls (never accessed) are the coldest.
        var candidates = await context.Renditions
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && (rendition.Status == RenditionStatus.Ready || rendition.Status == RenditionStatus.Stale))
            .OrderBy(rendition => rendition.Status == RenditionStatus.Stale ? 0 : 1)
            .ThenBy(rendition => rendition.LastAccessUtcMs ?? long.MinValue)
            .ThenBy(rendition => rendition.Id)
            .ToListAsync(cancellationToken);

        // No fallback to a protected candidate: a rendition with a live lease is never
        // a candidate, even when it is the only one left.
        return candidates.FirstOrDefault(candidate => !protectedIds.Contains(candidate.Id));
    }

    private async Task<long> EvictAsync(RenditionEntity rendition, CancellationToken cancellationToken)
    {
        var claimedStatus = rendition.Status;

        // Two-phase: claim with a revision check so a concurrent playback-session
        // selection loses instead of serving a file that is about to vanish.
        var claimed = await context.Renditions
            .Where(candidate => candidate.Id == rendition.Id
                && candidate.Status == claimedStatus
                && candidate.Revision == rendition.Revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, RenditionStatus.Evicting)
                .SetProperty(candidate => candidate.Revision, rendition.Revision + 1), cancellationToken);

        if (claimed == 0)
        {
            return 0;
        }

        var bytes = rendition.ByteLength;
        var deleted = await DeleteFilesAsync(rendition, cancellationToken);

        await context.Renditions
            .Where(candidate => candidate.Id == rendition.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, RenditionStatus.Evicted)
                .SetProperty(candidate => candidate.Revision, rendition.Revision + 2), cancellationToken);

        logger.LogInformation("Evicted quality rendition {RenditionId} ({Bytes} bytes).", rendition.Id, bytes);
        return deleted ? bytes : 0;
    }

    /// <summary>
    /// Deletes a quality rendition's managed files after proving they are ours. The catalog
    /// path alone is not trusted: it is resolved through the library path guard (which
    /// rejects escapes and any symlinked ancestor), required to sit inside the managed
    /// quality area, and matched against the manifest or the recorded byte length before
    /// anything is removed.
    /// </summary>
    private async Task<bool> DeleteFilesAsync(RenditionEntity rendition, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        if (!TryResolveManagedFile(rendition.RelativePath, out var outputPath))
        {
            logger.LogWarning(
                "Refusing to delete quality rendition {RenditionId}: {Path} is not a managed file inside the quality area.",
                rendition.Id, rendition.RelativePath);
            return false;
        }

        if (!IsOwnedByThisApplication(rendition, outputPath!))
        {
            logger.LogWarning(
                "Refusing to delete {Path} for rendition {RenditionId}: it is not recognizable as this application's output.",
                outputPath, rendition.Id);
            return false;
        }

        // Manifest first: it is what proves ownership of the output, and a manifest left
        // beside a deleted file would describe something that no longer exists.
        if (rendition.ManifestPath is not null && TryResolveManagedFile(rendition.ManifestPath, out var manifestPath))
        {
            TryDelete(manifestPath!, rendition.Id);
        }

        return TryDelete(outputPath!, rendition.Id);
    }

    private bool IsOwnedByThisApplication(RenditionEntity rendition, string outputPath)
    {
        if (PreparationArtifacts.IsOwnedOutput(appOptions.Value.LibraryRoot, rendition.RelativePath))
        {
            return true;
        }

        // The sidecar may be gone or unreadable. Inside the managed quality area the
        // recorded byte length is still enough to recognize our own output, and refusing
        // here would leak the file forever.
        try
        {
            var file = new FileInfo(outputPath);
            return file.Exists && file.LinkTarget is null && file.Length == rendition.ByteLength;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryDelete(string absolutePath, long renditionId)
    {
        try
        {
            File.Delete(absolutePath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The eviction of rendition {RenditionId} could not delete {Path}.", renditionId, absolutePath);
            return false;
        }
    }

    /// <summary>
    /// Resolves a catalog-relative path to a real file inside the managed quality area,
    /// or fails. Absolute paths are never accepted here — a managed artifact is always
    /// addressed relative to the library root.
    /// </summary>
    private bool TryResolveManagedFile(string relativePath, out string? absolutePath)
    {
        absolutePath = null;

        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || !PreparationArtifacts.IsInsideQualityArea(relativePath))
        {
            return false;
        }

        if (!LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, relativePath, out var resolved))
        {
            return false;
        }

        absolutePath = resolved;
        return true;
    }
}
