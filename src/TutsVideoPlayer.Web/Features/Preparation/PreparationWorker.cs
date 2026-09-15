using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Media;
using System.Globalization;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Library;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Web.Features.Preparation;

/// <summary>
/// The single-process preparation worker. It recovers interrupted work at boot,
/// respects the persisted queue pause, claims exactly one job at a time with a
/// conditional update so simultaneous claimants cannot double-run, and keeps
/// retry counts bounded. A disconnected request never cancels accepted work.
/// </summary>
public sealed class PreparationWorker(
    IServiceScopeFactory scopeFactory,
    FFmpegAdapter ffmpegAdapter,
    FFprobeAdapter probeAdapter,
    IOptions<PreparationOptions> preparationOptions,
    ILogger<PreparationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var readinessScope = scopeFactory.CreateScope())
        {
            if (!readinessScope.ServiceProvider.GetRequiredService<SchemaReadiness>().IsReady) return;
        }
        try
        {
            using var recoveryScope = scopeFactory.CreateScope();
            var recoveryContext = recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var recoveryExecutor = new PreparationExecutor(
                recoveryContext,
                recoveryScope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>(),
                preparationOptions,
                probeAdapter,
                ffmpegAdapter,
                new PreparedOutputValidator(probeAdapter),
                recoveryScope.ServiceProvider.GetRequiredService<ILogger<PreparationExecutor>>());

            await recoveryExecutor.RecoverInterruptedAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Preparation recovery failed at startup.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var preferences = await context.Preferences.AsNoTracking().SingleOrDefaultAsync(stoppingToken);
                if (preferences?.QueuePaused == true)
                {
                    await Task.Delay(IdlePoll, stoppingToken);
                    continue;
                }

                var claimed = await ClaimNextAsync(context, stoppingToken);
                if (claimed is null)
                {
                    await Task.Delay(IdlePoll, stoppingToken);
                    continue;
                }

                var executor = new PreparationExecutor(
                    context,
                    scope.ServiceProvider.GetRequiredService<IOptions<AppOptions>>(),
                    preparationOptions,
                    probeAdapter,
                    ffmpegAdapter,
                    new PreparedOutputValidator(probeAdapter),
                    scope.ServiceProvider.GetRequiredService<ILogger<PreparationExecutor>>());

                logger.LogInformation("Claimed preparation job {JobId} (attempt {Attempt}).", claimed.Id, claimed.Attempt);
                var outcome = await executor.ExecuteAsync(claimed.Id, stoppingToken);
                logger.LogInformation("Preparation job {JobId} finished as {State}.", claimed.Id, outcome.FinalState);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The preparation worker loop failed; retrying.");
                try
                {
                    await Task.Delay(IdlePoll, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task<PreparationJobEntity?> ClaimNextAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        var candidate = await context.PreparationJobs
            .Where(job => job.State == PreparationJobState.Queued)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.EnqueuedUtcMs)
            .ThenBy(job => job.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return null;
        }

        var owner = $"{Environment.MachineName}:{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lease = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();

        var claimed = await context.PreparationJobs
            .Where(job => job.Id == candidate.Id
                && job.State == PreparationJobState.Queued
                && job.Revision == candidate.Revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.State, PreparationJobState.Running)
                .SetProperty(job => job.Revision, candidate.Revision + 1)
                .SetProperty(job => job.StartedUtcMs, candidate.StartedUtcMs ?? now)
                .SetProperty(job => job.Attempt, candidate.Attempt + 1)
                .SetProperty(job => job.LeaseOwner, owner)
                .SetProperty(job => job.LeaseExpiresUtcMs, lease), cancellationToken);

        if (claimed == 0)
        {
            return null;
        }

        // ExecuteUpdate bypasses tracking, so the entity cached from the candidate
        // query still carries the pre-claim revision and would poison every later save.
        context.ChangeTracker.Clear();

        return await context.PreparationJobs
            .Include(job => job.Lesson)
            .ThenInclude(lesson => lesson.SourceComponents)
            .SingleAsync(job => job.Id == candidate.Id, cancellationToken);
    }
}