using VibeCast.Data;

namespace VibeCast.Downloads;

/// <summary>
/// Decides whether a newly-ingested episode should be auto-downloaded: RSS
/// enclosures only (YouTube never downloads), per-feed auto-download toggle, and
/// the per-feed max-age cutoff (null = no limit). Evaluated once at ingest time --
/// an episode that ages past the cutoff while still undownloaded simply never
/// auto-downloads; it is not retroactively purged. Also skips episodes already
/// marked played/archived (e.g. the back-catalog FeedSubscriptionService archives
/// on initial subscribe), so they aren't immediately re-queued.
/// </summary>
internal static class AutoDownloadGate
{
    public static bool ShouldAutoDownload(Feed feed, Episode episode)
    {
        if (episode.EnclosureUrl is null)
        {
            return false;
        }

        if (episode.IsPlayed || episode.IsArchived)
        {
            return false;
        }

        if (!feed.AutoDownloadEnabled)
        {
            return false;
        }

        if (feed.AutoDownloadMaxAgeDays is { } maxAgeDays)
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            if (episode.PublishedAtUtc < cutoff)
            {
                return false;
            }
        }

        return true;
    }
}
