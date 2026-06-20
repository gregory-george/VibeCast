using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeCast.AppHost;

namespace VibeCast.Data;

internal static class DatabaseLifecycle
{
    private const int MaxBackups = 10;

    /// <summary>
    /// Copies podcasts.db -> backups/podcasts-yyyyMMddHHmmss.db.bak before any migration
    /// runs, then prunes old backups. Must be called before the EF Core services are
    /// touched, since it's a plain file copy.
    /// </summary>
    public static void BackupBeforeMigration()
    {
        if (!File.Exists(AppPaths.DatabaseFile))
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.BackupsDirectory);
        File.Copy(AppPaths.DatabaseFile, AppPaths.DatabaseBackupFor(DateTime.Now), overwrite: true);
        PruneOldBackups();
    }

    private static void PruneOldBackups()
    {
        var backups = new DirectoryInfo(AppPaths.BackupsDirectory)
            .GetFiles(AppPaths.DatabaseBackupPattern)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(MaxBackups);

        foreach (var file in backups)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
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
