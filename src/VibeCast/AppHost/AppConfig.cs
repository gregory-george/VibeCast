using System.Text.Json;

namespace VibeCast.AppHost;

internal sealed class AppConfig
{
    public int? PreferredPort { get; set; }

    /// <summary>
    /// When the in-app player reaches the end of an episode, automatically mark it
    /// played (RSS: deletes the file too) instead of requiring a manual click.
    /// Default off.
    /// </summary>
    public bool AutoMarkOnCompletion { get; set; }

    /// <summary>Refresh all feeds automatically each time the app is opened. Default on.</summary>
    public bool RefreshOnOpen { get; set; } = true;

    /// <summary>Global keep-last-N backstop, overridable per feed (<see cref="VibeCast.Data.Feed.KeepLastCount"/>).</summary>
    public int DefaultKeepLastCount { get; set; } = 100;

    /// <summary>Global auto-download age cutoff in days, used as the default for newly-added feeds.</summary>
    public int DefaultAutoDownloadMaxAgeDays { get; set; } = 90;

    /// <summary>Default "exclude Shorts" setting applied when a new YouTube feed is added.</summary>
    public bool DefaultExcludeShorts { get; set; }

    /// <summary>Starting playback speed for newly-opened episodes (RSS: exact; YouTube: nearest available step).</summary>
    public decimal DefaultPlaybackSpeed { get; set; } = 1.0m;

    /// <summary>How many enclosures the download worker streams at once.</summary>
    public int ConcurrentDownloadLimit { get; set; } = 1;

    /// <summary>Show the tray icon (running indicator + Reopen UI + Quit). Default on.</summary>
    public bool TrayEnabled { get; set; } = true;

    /// <summary>
    /// Seconds the host waits with zero open circuits before shutting down (see
    /// <see cref="VibeCast.Shutdown.ShutdownCoordinatorService"/>); survives a quick
    /// refresh/reconnect blip without staying alive indefinitely.
    /// </summary>
    public int GraceWindowSeconds { get; set; } = 20;

    /// <summary>
    /// True once the first-run "create a desktop shortcut?" prompt has been shown
    /// (regardless of answer), so it never asks again. config.json not existing yet
    /// is what identifies first run -- see <see cref="Load"/>.
    /// </summary>
    public bool HasOfferedDesktopShortcut { get; set; }

    public static AppConfig Load()
    {
        if (!File.Exists(AppPaths.ConfigFile))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(AppPaths.ConfigFile);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.ConfigFile, json);
    }
}
