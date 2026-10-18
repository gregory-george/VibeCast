using Microsoft.EntityFrameworkCore;
using VibeCast.Data;

namespace VibeCast.Episodes;

/// <summary>
/// The library page's episode-list filters (see <see cref="VibeCast.Components.Pages.Home"/>),
/// persisted in <see cref="VibeCast.AppHost.AppConfig.EpisodeFilters"/> so they survive a
/// restart. Each checked box *includes* its slice of episodes. Boxes in the same group
/// are OR'd and the groups are AND'd, so unchecking both boxes in a group empties the
/// list. Every episode falls in exactly one slice per group:
/// <list type="bullet">
/// <item>Status: <see cref="Unplayed"/> (not archived) / <see cref="Archived"/>.</item>
/// <item>Type: <see cref="Shorts"/> (YouTube, no enclosure, 5 min or less) / <see cref="Longs"/> (everything else, podcasts included).</item>
/// <item>Source: RSS <see cref="Downloaded"/> (file on disk) / RSS <see cref="NotDownloaded"/>,
/// YouTube <see cref="Streamed"/> (aired) / YouTube <see cref="Upcoming"/> (premiere not yet aired).</item>
/// </list>
/// <see cref="InProgressOnly"/> is the one restricting filter: when set, only unplayed
/// episodes with a saved resume position remain. The defaults reproduce the plain
/// active list.
/// </summary>
internal sealed class EpisodeFilters
{
    /// <summary>Max duration of a YouTube video still counted as a Short.</summary>
    public const int ShortMaxSeconds = 300;

    public bool Unplayed { get; set; } = true;

    public bool Archived { get; set; }

    public bool Shorts { get; set; } = true;

    public bool Longs { get; set; } = true;

    public bool Downloaded { get; set; } = true;

    public bool NotDownloaded { get; set; } = true;

    public bool Streamed { get; set; } = true;

    public bool Upcoming { get; set; } = true;

    public bool InProgressOnly { get; set; }

    /// <summary>How many filters differ from their defaults, shown on the Filters button.</summary>
    public int ChangedCount()
    {
        var defaults = new EpisodeFilters();
        return (Unplayed != defaults.Unplayed ? 1 : 0)
            + (Archived != defaults.Archived ? 1 : 0)
            + (Shorts != defaults.Shorts ? 1 : 0)
            + (Longs != defaults.Longs ? 1 : 0)
            + (Downloaded != defaults.Downloaded ? 1 : 0)
            + (NotDownloaded != defaults.NotDownloaded ? 1 : 0)
            + (Streamed != defaults.Streamed ? 1 : 0)
            + (Upcoming != defaults.Upcoming ? 1 : 0)
            + (InProgressOnly != defaults.InProgressOnly ? 1 : 0);
    }

    public void Reset()
    {
        var defaults = new EpisodeFilters();
        Unplayed = defaults.Unplayed;
        Archived = defaults.Archived;
        Shorts = defaults.Shorts;
        Longs = defaults.Longs;
        Downloaded = defaults.Downloaded;
        NotDownloaded = defaults.NotDownloaded;
        Streamed = defaults.Streamed;
        Upcoming = defaults.Upcoming;
        InProgressOnly = defaults.InProgressOnly;
    }

    /// <summary>
    /// Translates the filters (plus an optional case-insensitive title search) into SQL.
    /// The Downloaded/Not downloaded split here goes by the <see cref="Episode.IsDownloaded"/>
    /// flag; the flag can outlive its file (a deferred locked delete), so callers refine
    /// with <see cref="MatchesSource"/> once they've checked the disk.
    /// </summary>
    public IQueryable<Episode> Apply(IQueryable<Episode> query, string? titleSearch)
    {
        // Snapshot into locals: this instance is a shared config singleton another circuit
        // could flip mid-query.
        var (unplayed, archived) = (Unplayed, Archived);
        var (shorts, longs) = (Shorts, Longs);
        var (downloaded, notDownloaded, streamed, upcoming) = (Downloaded, NotDownloaded, Streamed, Upcoming);

        if (!unplayed || !archived)
        {
            query = query.Where(e => (unplayed && !e.IsArchived) || (archived && e.IsArchived));
        }

        if (!shorts || !longs)
        {
            query = query.Where(e =>
                (e.EnclosureUrl == null && e.DurationSeconds != null && e.DurationSeconds <= ShortMaxSeconds) ? shorts : longs);
        }

        if (!downloaded || !notDownloaded || !streamed || !upcoming)
        {
            // A flagged-downloaded row may still be "not downloaded" if its file is gone,
            // so Not downloaded lets every RSS row through and the disk check decides.
            query = query.Where(e => e.YouTubeVideoId == null
                ? notDownloaded || (downloaded && e.IsDownloaded)
                : e.ScheduledStartUtc == null ? streamed : upcoming);
        }

        if (InProgressOnly)
        {
            query = query.Where(e => !e.IsPlayed && e.PlaybackPositionSeconds > 0);
        }

        if (!string.IsNullOrWhiteSpace(titleSearch))
        {
            // SQLite LIKE is case-insensitive for ASCII; escape the user's own wildcards.
            var escaped = titleSearch.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            query = query.Where(e => EF.Functions.Like(e.Title, "%" + escaped + "%", @"\"));
        }

        return query;
    }

    /// <summary>Final source check once the caller knows whether an RSS file is actually on disk.</summary>
    public bool MatchesSource(bool isYouTube, bool isUpcoming, bool isOnDisk) =>
        isYouTube
            ? (isUpcoming ? Upcoming : Streamed)
            : (isOnDisk ? Downloaded : NotDownloaded);
}
