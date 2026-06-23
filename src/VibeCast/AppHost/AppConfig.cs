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

    /// <summary>
    /// On initial RSS subscribe, only the newest N episodes are left active for
    /// auto-download; the rest of the back catalog is pre-archived (see
    /// <see cref="VibeCast.Feeds.FeedSubscriptionService"/>) so a feed with years of
    /// history doesn't flood downloads/disk on day one. Not applied to YouTube: its
    /// feed already returns only ~15 most recent items.
    /// </summary>
    public int InitialActiveEpisodeCount { get; set; } = 15;

    /// <summary>Default "exclude Shorts" setting applied when a new YouTube feed is added.</summary>
    public bool DefaultExcludeShorts { get; set; }

    /// <summary>Starting playback speed for newly-opened episodes (RSS: exact; YouTube: nearest available step).</summary>
    public decimal DefaultPlaybackSpeed { get; set; } = 1.0m;

    /// <summary>
    /// Seconds the left/right arrow keys jump backward/forward during playback (RSS and
    /// YouTube). Default 10.
    /// </summary>
    public int SkipSeconds { get; set; } = 10;

    /// <summary>
    /// Whether closed captions start on for newly-opened YouTube videos. RSS has no
    /// caption tracks to toggle, so this only affects the YouTube embed. Default on.
    /// </summary>
    public bool ClosedCaptionsEnabled { get; set; } = true;

    /// <summary>How many enclosures the download worker streams at once.</summary>
    public int ConcurrentDownloadLimit { get; set; } = 1;

    /// <summary>Show the tray icon (running indicator + Reopen UI + Quit). Default on.</summary>
    public bool TrayEnabled { get; set; } = true;

    /// <summary>
    /// Seconds the host waits with zero open circuits before shutting down (see
    /// <see cref="VibeCast.Shutdown.ShutdownCoordinatorService"/>); survives a quick
    /// refresh/reconnect blip without staying alive indefinitely.
    /// </summary>
    public int GraceWindowSeconds { get; set; } = 10;

    /// <summary>
    /// True once the first-run "create a desktop shortcut?" prompt has been shown
    /// (regardless of answer), so it never asks again. config.json not existing yet
    /// is what identifies first run -- see <see cref="Load"/>.
    /// </summary>
    public bool HasOfferedDesktopShortcut { get; set; }

    /// <summary>
    /// Sticky height (px) of the small (non-theater) now-playing video player, set by
    /// dragging the resize handle (see <see cref="VibeCast.Components.Layout.NowPlaying"/>).
    /// Width follows from the player's aspect ratio. Video only -- audio has no
    /// resizable player, and theater mode is unaffected. Default matches the
    /// stylesheet's original fixed small-player height.
    /// </summary>
    public int VideoPlayerHeightPx { get; set; } = 150;

    /// <summary>
    /// Hide feeds with zero active (non-archived) episodes from the sidebar feed list
    /// on the library page (see <see cref="VibeCast.Components.Pages.Home"/>). Default off.
    /// </summary>
    public bool HideEmptyFeeds { get; set; }

    /// <summary>
    /// UI color theme: "Light", "Dark", or "System" (follows the OS via
    /// <c>prefers-color-scheme</c>). Default "System".
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Episode list sort order on the library page (see
    /// <see cref="VibeCast.Components.Pages.Home"/>): newest-first when true, oldest-first
    /// when false. Default true (newest-first).
    /// </summary>
    public bool EpisodeSortDescending { get; set; } = true;

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
