using Microsoft.Extensions.Diagnostics.HealthChecks;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Persistence;

public sealed class DatabaseReadinessCheck(SchemaReadiness readiness) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        readiness.IsReady
            ? Task.FromResult(HealthCheckResult.Healthy("Database schema is up to date."))
            : Task.FromResult(HealthCheckResult.Unhealthy(
                "Database schema is pending; run the 'migrate' maintenance command.",
                data: new Dictionary<string, object> { ["pendingMigrations"] = readiness.PendingMigrations.Count }));
}
