using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Library;
using TutsVideoPlayer.Web.Features.Preparation;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Features.Subtitles;
namespace TutsVideoPlayer.Web.Hosting;

public static class CatalogRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, AppOptions options, PreparationOptions preparationOptions)
    {
        // The worker and executor consume IOptions<PreparationOptions>; registering only the
        // concrete instance left them with silent defaults (e.g. the configured disk reserve
        // was never enforced).
        services.AddSingleton(Options.Create(preparationOptions));
        services.AddSingleton(new FFmpegAdapter(preparationOptions.FFmpegPath));
        services.AddScoped<PreparedOutputValidator>();
        // Registered as a hosted service so the lock is actually acquired, and registered
        // before the scanner and worker so single-process ownership is established before
        // any recovery or scheduling runs. Hosted services start in registration order.
        services.AddHostedService<InstallationLockHolder>();
        var databasePath = ResolvePath(options.DataDirectory, "tutsvideoplayer.db");
        var dataDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        services.AddDbContext<AppDbContext>(dbOptions =>
            dbOptions.UseSqlite($"Data Source={databasePath}"));

        services.AddSingleton<ILibraryFileEnumerator, FileSystemLibraryEnumerator>();
        services.AddSingleton(new FFprobeAdapter(options.FFprobePath));
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<LibraryScanner>();
        services.AddScoped<LearningService>();
        services.AddScoped<SubtitleService>();
        services.AddScoped<CacheAccounting>();
        services.AddScoped<CacheEvictionService>();
        services.AddSingleton<ScanCoordinator>();
        services.AddHostedService<StartupMigrationCheck>();
        services.AddHostedService<StartupScanService>();
        services.AddHostedService<PreparationWorker>();

        return services;
    }

    public static string ResolvePath(string configured, string fileName) =>
        Path.IsPathRooted(configured)
            ? Path.Combine(configured, fileName)
            : Path.Combine(AppContext.BaseDirectory, configured, fileName);
}
