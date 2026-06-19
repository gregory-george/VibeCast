using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeCast.AppHost;

namespace VibeCast.Data;

internal static class DatabaseLifecycle
{
    /// <summary>
    /// Copies podcasts.db -> podcasts.db.bak before any migration runs. Must be
    /// called before the EF Core services are touched, since it's a plain file copy.
    /// </summary>
    public static void BackupBeforeMigration()
    {
        if (File.Exists(AppPaths.DatabaseFile))
        {
            File.Copy(AppPaths.DatabaseFile, AppPaths.DatabaseBackupFile, overwrite: true);
        }
    }

    public static async Task MigrateAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    /// <summary>WAL checkpoint on shutdown, per the hard rule in CLAUDE.md.</summary>
    public static async Task CheckpointAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
    }
}
