using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Web.Features.Library;

public sealed record ScanStartResult(long ScanRunId, string State, bool Joined);

public sealed class ScanCoordinator(
    IServiceScopeFactory scopeFactory,
    IOptions<AppOptions> options,
    ILogger<ScanCoordinator> logger)
{
    private readonly SemaphoreSlim _claimLock = new(1, 1);

    public async Task<ScanStartResult> StartOrJoinAsync(CancellationToken cancellationToken = default)
    {
        await _claimLock.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await EnsureLibraryAsync(context, cancellationToken);

            var activeRun = await context.ScanRuns
                .AsNoTracking()
                .SingleOrDefaultAsync(run => run.State == "Running", cancellationToken);

            if (activeRun is not null)
            {
                return new ScanStartResult(activeRun.Id, activeRun.State, Joined: true);
            }

            var run = new ScanRunEntity
            {
                StartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                State = "Running"
            };
            context.ScanRuns.Add(run);
            await context.SaveChangesAsync(cancellationToken);

            _ = Task.Run(() => ExecuteAsync(run.Id, CancellationToken.None));
            return new ScanStartResult(run.Id, run.State, Joined: false);
        }
        finally
        {
            _claimLock.Release();
        }
    }

    public async Task RecoverAbandonedScansAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var abandoned = await context.ScanRuns
            .Where(run => run.State == "Running")
            .ToListAsync(cancellationToken);

        foreach (var run in abandoned)
        {
            run.State = "Failed";
            run.FinishedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            context.ScanIssues.Add(new ScanIssueEntity
            {
                ScanRun = run,
                Code = "AbandonedByRestart",
                Message = "The application restarted while this scan was running."
            });
        }

        if (abandoned.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Marked {Count} abandoned scan run(s) as failed after restart.", abandoned.Count);
        }
    }

    private static async Task EnsureLibraryAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        var exists = await context.Libraries.AnyAsync(cancellationToken);
        if (exists)
        {
            return;
        }

        context.Libraries.Add(new LibraryEntity
        {
            DisplayName = "Tutorial library",
            LogicalIdentity = Guid.NewGuid().ToString()
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task ExecuteAsync(long scanRunId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var library = await context.Libraries.SingleAsync(cancellationToken);
            var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();

            await scanner.ExecuteAsync(scanRunId, library, options.Value.LibraryRoot, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Background scan execution {ScanRunId} failed unexpectedly.", scanRunId);
            try
            {
                using var scope = scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var run = await context.ScanRuns.SingleAsync(r => r.Id == scanRunId, CancellationToken.None);
                if (run.State == "Running")
                {
                    run.State = "Failed";
                    run.FinishedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await context.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch
            {
            }
        }
    }
}
