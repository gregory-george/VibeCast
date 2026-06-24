using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeCast.AppHost;

namespace VibeCast.Data;

internal static class DatabaseLifecycle
{
    private const int MaxBackups = 10;

    /// <summary>
    /// Copies podcasts.db -> backups/podcasts-yyyyMMdd.db.bak before any migration
    /// runs (at most once per calendar day), then prunes old backups. Must be called
    /// before the EF Core services are touched, since it's a plain file copy.
    /// </summary>
    public static void BackupBeforeMigration()
    {
        Directory.CreateDirectory(AppPaths.BackupsDirectory);
        BackupFile(AppPaths.DatabaseFile, AppPaths.DatabaseBackupFor(DateTime.Today), AppPaths.DatabaseBackupPattern);
        BackupFile(AppPaths.ConfigFile, AppPaths.ConfigBackupFor(DateTime.Today), AppPaths.ConfigBackupPattern);
    }

    private static void BackupFile(string sourcePath, string backupPath, string prunePattern)
    {
        if (!File.Exists(sourcePath) || File.Exists(backupPath))
        {
            return;
        }

        File.Copy(sourcePath, backupPath);
        PruneOldBackups(prunePattern);
    }

    private static void PruneOldBackups(string pattern)
    {
        var backups = new DirectoryInfo(AppPaths.BackupsDirectory)
            .GetFiles(pattern)
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
