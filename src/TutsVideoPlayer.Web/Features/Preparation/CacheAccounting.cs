using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Features.Preparation;

public sealed record StorageSummaryModel(
    long PermanentBytes,
    long QualityReadyBytes,
    long QualityReservedBytes,
    long QualityProtectedBytes,
    long CacheLimitBytes,
    long AvailableDiskBytes,
    long? PendingCleanupBytes);

public sealed class CacheAccounting(AppDbContext context, IOptions<AppOptions> appOptions)
{
    /// <summary>Heartbeat grace: a session that heartbeated this recently still protects its rendition.</summary>
    public static readonly long LeaseGraceMs = 60_000;

    public const long MinimumCacheLimitBytes = 1024;

    public async Task<long> GetCacheLimitAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await context.Preferences.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return preferences?.CacheLimitBytes ?? 20_000_000_000;
    }

    /// <summary>
    /// Ready quality bytes: the chargeable, evictable content under the budget.
    /// </summary>
    public async Task<long> GetReadyQualityBytesAsync(CancellationToken cancellationToken = default) =>
        await context.Renditions.AsNoTracking()
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && rendition.Status == RenditionStatus.Ready)
            .SumAsync(rendition => rendition.ByteLength, cancellationToken);

    /// <summary>
    /// Reserved bytes for in-flight quality work: the larger of the recorded
    /// reservation and the current temporary file size, per attempt.
    /// </summary>
    public async Task<long> GetReservedQualityBytesAsync(CancellationToken cancellationToken = default)
    {
        var activeJobs = await context.PreparationJobs.AsNoTracking()
            .Where(job => job.Purpose == RenditionPurpose.Quality
                && (job.State == PreparationJobState.Queued
                    || job.State == PreparationJobState.Running
                    || job.State == PreparationJobState.Validating
                    || job.State == PreparationJobState.Publishing))
            .Select(job => new { job.Id, job.ReservedBytes, job.OutputRelativePath })
            .ToListAsync(cancellationToken);

        long total = 0;
        foreach (var job in activeJobs)
        {
            var current = 0L;
            if (job.OutputRelativePath is not null)
            {
                var absolute = Path.IsPathRooted(job.OutputRelativePath)
                    ? job.OutputRelativePath
                    : Path.Combine(appOptions.Value.LibraryRoot, job.OutputRelativePath);
                var info = new FileInfo(absolute);
                if (info.Exists)
                {
                    current = info.Length;
                }
            }

            total += Math.Max(job.ReservedBytes ?? 0, current);
        }

        return total;
    }

    public async Task<StorageSummaryModel> GetStorageSummaryAsync(CancellationToken cancellationToken = default)
    {
        var permanentBytes = await context.Renditions.AsNoTracking()
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Permanent
                && rendition.Status == RenditionStatus.Ready)
            .SumAsync(rendition => rendition.ByteLength, cancellationToken);

        var qualityReady = await context.Renditions.AsNoTracking()
            .Where(rendition => rendition.RetentionClass == RenditionRetention.Quality
                && rendition.Status == RenditionStatus.Ready)
            .SumAsync(rendition => rendition.ByteLength, cancellationToken);

        var reserved = await GetReservedQualityBytesAsync(cancellationToken);
        var limit = await GetCacheLimitAsync(cancellationToken);
        var available = GetAvailableDiskBytes();

        var protectedBytes = await GetProtectedQualityBytesAsync(cancellationToken);

        // The shortfall is reported even while every byte is protected: the leases
        // release and the cleanup completes, so this is genuinely pending work.
        var pending = Math.Max(0, qualityReady + reserved - limit);

        return new StorageSummaryModel(permanentBytes, qualityReady, reserved, protectedBytes, limit, available, pending);
    }

    public async Task<long> GetProtectedQualityBytesAsync(CancellationToken cancellationToken = default)
    {
        var graceCutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LeaseGraceMs;

        var protectedRenditionIds = await context.PlaybackSessions.AsNoTracking()
            .Where(session => session.ClosedUtcMs == null
                && session.ActiveRenditionId != null
                && session.LastHeartbeatUtcMs >= graceCutoff)
            .Select(session => session.ActiveRenditionId!.Value)
            .ToListAsync(cancellationToken);

        if (protectedRenditionIds.Count == 0)
        {
            return 0;
        }

        return await context.Renditions.AsNoTracking()
            .Where(rendition => protectedRenditionIds.Contains(rendition.Id)
                && rendition.RetentionClass == RenditionRetention.Quality
                && rendition.Status == RenditionStatus.Ready)
            .SumAsync(rendition => rendition.ByteLength, cancellationToken);
    }

    public long GetAvailableDiskBytes()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(appOptions.Value.LibraryRoot)) ?? "/";
        var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
            string.Equals(candidate.Name.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        return drive?.AvailableFreeSpace ?? 0;
    }

    public string DescribeBytes(long bytes) =>
        (bytes / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
}