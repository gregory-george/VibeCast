namespace VibeCast.AppHost;

internal static class AppPaths
{
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    public static string ConfigFile => Path.Combine(BaseDirectory, "config.json");

    public static string RunLockFile => Path.Combine(BaseDirectory, "run.lock");

    public static string DatabaseFile => Path.Combine(BaseDirectory, "podcasts.db");

    public static string BackupsDirectory => Path.Combine(BaseDirectory, "backups");

    public const string DatabaseBackupPattern = "podcasts-*.db.bak";

    public static string DatabaseBackupFor(DateTime timestamp) =>
        Path.Combine(BackupsDirectory, $"podcasts-{timestamp:yyyyMMddHHmmss}.db.bak");

    public static string DownloadsDirectory => Path.Combine(BaseDirectory, "downloads");

    public static string LogsDirectory => Path.Combine(BaseDirectory, "logs");
}
