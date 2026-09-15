using Microsoft.Extensions.Options;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Hosting;

/// <summary>
/// Holds the single-process installation lock for the lifetime of the host. A
/// second process pointed at the same application data directory fails to start
/// instead of racing the same SQLite database and job coordinator.
/// </summary>
public sealed class InstallationLockHolder(IOptions<AppOptions> options) : IHostedService
{
    private readonly string _appDataDirectory = Resolve(options.Value.DataDirectory);
    private InstallationLock? _lock;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _lock = InstallationLock.Acquire(_appDataDirectory);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another TutsVideoPlayer process already owns the application data directory '{_appDataDirectory}'. "
                + "Only one host may run against the same data; stop the other process or point this one at a different App:DataDirectory.",
                exception);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _lock?.Dispose();
        _lock = null;
        return Task.CompletedTask;
    }

    private static string Resolve(string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
}