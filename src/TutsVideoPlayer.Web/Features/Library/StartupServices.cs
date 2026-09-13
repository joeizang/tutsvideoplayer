using TutsVideoPlayer.Infrastructure.Persistence;

namespace TutsVideoPlayer.Web.Features.Library;

public sealed class SchemaReadiness
{
    public bool IsReady { get; private set; }

    public IReadOnlyList<string> PendingMigrations { get; private set; } = [];

    public void MarkReady() => IsReady = true;

    public void MarkPending(IReadOnlyList<string> pendingMigrations)
    {
        IsReady = false;
        PendingMigrations = pendingMigrations;
    }
}

public sealed class StartupMigrationCheck(
    IServiceScopeFactory scopeFactory,
    SchemaReadiness readiness,
    ILogger<StartupMigrationCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();

        try
        {
            var pending = await initializer.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Count == 0)
            {
                readiness.MarkReady();
                logger.LogInformation("Database schema is up to date.");
            }
            else
            {
                readiness.MarkPending([.. pending]);
                logger.LogError(
                    "The database schema is {Count} migration(s) behind. Run the application with the 'migrate' maintenance command before serving.",
                    pending.Count);
            }
        }
        catch (Exception exception)
        {
            readiness.MarkPending(["<unavailable>"]);
            logger.LogError(exception, "The database schema state could not be checked.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class StartupScanService(
    ScanCoordinator coordinator,
    SchemaReadiness readiness,
    ILogger<StartupScanService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            if (!readiness.IsReady)
            {
                logger.LogWarning("Startup scan skipped because the database schema is not ready.");
                return;
            }

            await coordinator.RecoverAbandonedScansAsync(stoppingToken);
            var result = await coordinator.StartOrJoinAsync(stoppingToken);
            logger.LogInformation("Startup scan {ScanRunId} {Mode}.", result.ScanRunId, result.Joined ? "joined an existing run" : "started");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The startup scan could not be started.");
        }
    }
}
