using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Library;
namespace TutsVideoPlayer.Web.Hosting;

public static class CatalogRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, AppOptions options)
    {
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
        services.AddSingleton<ScanCoordinator>();
        services.AddHostedService<StartupScanService>();
        services.AddHostedService<StartupMigrationCheck>();

        return services;
    }

    public static string ResolvePath(string configured, string fileName) =>
        Path.IsPathRooted(configured)
            ? Path.Combine(configured, fileName)
            : Path.Combine(AppContext.BaseDirectory, configured, fileName);
}
