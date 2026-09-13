using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TutsVideoPlayer.Infrastructure.Persistence;
using System.Net.Http.Json;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

[CollectionDefinition(TestApplication.CollectionName)]
public sealed class WebApplicationCollection : ICollectionFixture<TestApplication>
{
}

public sealed class TestApplication : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string CollectionName = "TutsVideoPlayerWebApplication";

    public async ValueTask InitializeAsync()
    {
        FixtureLibraryBuilder.Build(FixtureRoot);
        var databasePath = CatalogRegistration.ResolvePath(DataDirectory, "tutsvideoplayer.db");
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(FixtureRoot, recursive: true);
        Directory.Delete(DataDirectory, recursive: true);
    }

    public async Task<ScanStatusModel> EnsureCatalogScannedAsync()
    {
        var client = CreateClient();
        var start = await client.PostAsync("/api/v1/library/scans", null, TestContext.Current.CancellationToken);
        var started = await start.Content.ReadFromJsonAsync<ScanStartResultModel>(cancellationToken: TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            var scan = await client.GetFromJsonAsync<ScanStatusModel>($"/api/v1/library/scans/{started!.ScanRunId}", TestContext.Current.CancellationToken);
            if (scan is not null && scan.State != "Running")
            {
                return scan;
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The scan did not finish in time.");
    }

    public string FixtureRoot { get; } = Directory.CreateTempSubdirectory("tuts-endpoints-").FullName;

    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("tuts-appdata-").FullName;

    public CommandCaptureLoggerProvider CommandCapture { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("App:LibraryRoot", FixtureRoot);
        builder.UseSetting("App:DataDirectory", DataDirectory);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Debug);
            logging.AddProvider(CommandCapture);
        });
    }
}

public sealed class CommandCaptureLoggerProvider : ILoggerProvider
{
    private long _commandCount;

    public void Reset() => Interlocked.Exchange(ref _commandCount, 0);

    public long CommandCount => Interlocked.Read(ref _commandCount);

    private void Increment() => Interlocked.Increment(ref _commandCount);

    public ILogger CreateLogger(string categoryName) => new CommandCountLogger(this, categoryName);

    public void Dispose() { }

    private sealed class CommandCountLogger(CommandCaptureLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == "Microsoft.EntityFrameworkCore.Database.Command" && logLevel >= LogLevel.Information)
            {
                owner.Increment();
            }
        }
    }
}