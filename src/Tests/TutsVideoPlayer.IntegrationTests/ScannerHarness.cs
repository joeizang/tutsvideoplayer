using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.IntegrationTests;

public sealed class ScannerHarness : IAsyncLifetime
{
    public string Root { get; } = Directory.CreateTempSubdirectory("tuts-scan-").FullName;
    public AppDbContext Context { get; private set; } = null!;
    public LibraryScanner Scanner { get; private set; } = null!;
    public FailureInjectingEnumerator Enumerator { get; private set; } = null!;
    public InMemoryLogger<LibraryScanner> Logger { get; } = new();
    private SqliteConnection? _connection;

    public async ValueTask InitializeAsync()
    {
        FixtureLibraryBuilder.Build(Root);
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuts-scan-{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        Context = new AppDbContext(options);
        await Context.Database.MigrateAsync();

        Enumerator = new FailureInjectingEnumerator(new FileSystemLibraryEnumerator());
        UseProbe("ffprobe");
    }

    /// <summary>
    /// Rebuilds the scanner around a different probe executable so that probe failure and
    /// later recovery can be exercised without a real media fixture.
    /// </summary>
    public void UseProbe(string ffprobePath) =>
        Scanner = new LibraryScanner(Context, Enumerator, new FFprobeAdapter(ffprobePath), Logger);

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        _connection?.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    public async Task<LibraryEntity> EnsureLibraryAsync()
    {
        var library = await Context.Libraries.SingleOrDefaultAsync();
        if (library is null)
        {
            library = new LibraryEntity
            {
                DisplayName = "Tutorial library",
                LogicalIdentity = Guid.NewGuid().ToString()
            };
            Context.Libraries.Add(library);
            await Context.SaveChangesAsync();
        }

        return library;
    }

    public async Task<ScanRunEntity> RunScanAsync(CancellationToken cancellationToken = default)
    {
        var library = await EnsureLibraryAsync();
        var run = new ScanRunEntity
        {
            StartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = "Running"
        };
        Context.ScanRuns.Add(run);
        await Context.SaveChangesAsync(cancellationToken);
        await Scanner.ExecuteAsync(run.Id, library, Root, cancellationToken);
        return run;
    }
}

public sealed class FailureInjectingEnumerator(ILibraryFileEnumerator inner) : ILibraryFileEnumerator
{
    public HashSet<string> FailingDirectories { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Extra entries appended to every enumeration. Repeating an existing subtitle path is
    /// the cheapest way to make reconciliation throw after the scan transaction is open.
    /// </summary>
    public List<LibraryFileEntry> ExtraFiles { get; } = [];

    public LibraryEnumeration Enumerate(string root, CancellationToken cancellationToken = default)
    {
        var result = inner.Enumerate(root, cancellationToken);
        result.Files.AddRange(ExtraFiles);

        if (FailingDirectories.Count == 0)
        {
            return result;
        }

        var removed = result.Files
            .Where(file => FailingDirectories.Any(failing =>
                file.RelativePath.StartsWith($"{failing}/", StringComparison.Ordinal)))
            .ToList();
        foreach (var file in removed)
        {
            result.Files.Remove(file);
        }

        foreach (var failing in FailingDirectories)
        {
            result.FailedDirectories.Add(failing);
            result.Issues.Add(new LibraryEnumerationIssue(
                failing,
                "DirectoryEnumerationFailed",
                "The directory could not be read: UnauthorizedAccessException."));
        }

        return result;
    }
}

public sealed class InMemoryLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}