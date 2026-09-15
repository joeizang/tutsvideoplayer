namespace TutsVideoPlayer.Infrastructure.FileSystem;

/// <summary>
/// Single-process installation ownership. A second process pointed at the same
/// application data directory refuses to start instead of corrupting shared
/// SQLite state and double-running the job coordinator.
/// </summary>
public sealed class InstallationLock : IDisposable
{
    private FileStream? _stream;

    public static InstallationLock Acquire(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        var lockPath = Path.Combine(appDataDirectory, "install.lock");

        var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        return new InstallationLock { _stream = stream };
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}