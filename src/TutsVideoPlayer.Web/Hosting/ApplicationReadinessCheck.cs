using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TutsVideoPlayer.Web.Hosting;

public sealed class ApplicationReadinessCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["schema"] = "not established; database arrives in milestone 1"
        };

        return Task.FromResult(HealthCheckResult.Healthy("Host is serving requests.", data));
    }
}
