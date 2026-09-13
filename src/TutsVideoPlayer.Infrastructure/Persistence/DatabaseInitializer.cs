using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Persistence;

public sealed class DatabaseInitializer(AppDbContext context)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await context.Database.MigrateAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(CancellationToken cancellationToken = default) =>
        [.. await context.Database.GetPendingMigrationsAsync(cancellationToken)];
}
