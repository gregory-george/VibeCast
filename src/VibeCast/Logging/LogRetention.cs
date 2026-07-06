using System.Globalization;

namespace VibeCast.Logging;

/// <summary>
/// Prunes daily log files (logs/vibecast-YYYYMMDD.log) older than a retention window, run
/// once at startup. Backups are capped by count (see DatabaseLifecycle); logs are capped by
/// age instead, so a chatty day doesn't evict a quiet week. Without this the logs folder
/// grows without bound -- a slow disk leak in an app whose folder *is* the app. Best-effort:
/// never throws, so housekeeping can't block startup.
/// </summary>
internal static class LogRetention
{
    public const int DefaultMaxAgeDays = 30;

    private const string Prefix = "vibecast-";
    private const string Extension = ".log";

    public static void PruneOldLogs(string logsDirectory, int maxAgeDays = DefaultMaxAgeDays)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return;
        }

        // "Older than N days" keys off the date embedded in the filename (the truest signal
        // of which day a log is for), not the file's last-write time. A file exactly N days
        // old is kept; only strictly older ones are removed.
        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-maxAgeDays);

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(logsDirectory).GetFiles($"{Prefix}*{Extension}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (!TryParseLogDate(file.Name, out var logDate) || logDate >= cutoff)
            {
                continue;
            }

            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or permission-denied -- skip; the next startup retries.
            }
        }
    }

    private static bool TryParseLogDate(string fileName, out DateOnly date)
    {
        date = default;
        if (!fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var datePart = fileName[Prefix.Length..^Extension.Length];
        return DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
