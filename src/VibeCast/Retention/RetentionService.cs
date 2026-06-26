using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;

namespace VibeCast.Retention;

/// <summary>
/// The keep-last-N backstop (CLAUDE.md): runaway-disk guard against the additive
/// feed model plus auto-download-all-by-default. Caps downloaded files on disk per
/// feed, not DB rows -- records are kept forever regardless. Evicts old downloads
/// even if never marked played. RSS only; YouTube never downloads files. Runs on
/// refresh (per feed, from FeedRefreshService) and on shutdown (all feeds, from
/// HostRunner).
/// </summary>
internal sealed class RetentionService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    DownloadProgressTracker progressTracker,
    AppConfig config,
    ILogger<RetentionService> logger)
{
    public async Task EnforceAllFeedsAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var feedIds = await db.Feeds.Where(f => f.Type == FeedType.Rss).Select(f => f.Id).ToListAsync(ct);

        foreach (var feedId in feedIds)
        {
            await EnforceFeedAsync(feedId, ct);
        }
    }

    public async Task EnforceFeedAsync(int feedId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var feed = await db.Feeds.FindAsync([feedId], ct);
        if (feed is null || feed.Type != FeedType.Rss)
        {
            return;
        }

        // Retry any deletes that mark-as-played deferred because the file was locked
        // (still open in the player). Runs before the keep-last-N pass so a played file
        // doesn't survive on disk just because it's within the newest N.
        await RetryDeferredDeletionsAsync(db, feed, ct);

        var keepLast = feed.KeepLastCount ?? config.DefaultKeepLastCount;
        if (keepLast <= 0)
        {
            return;
        }

        var downloaded = await db.Episodes
            .Where(e => e.FeedId == feedId && e.IsDownloaded && e.DownloadedFileName != null)
            .OrderByDescending(e => e.PublishedAtUtc)
            .ToListAsync(ct);

        if (downloaded.Count <= keepLast)
        {
            return;
        }

        foreach (var episode in downloaded.Skip(keepLast))
        {
            var filePath = DownloadFileStore.PathFor(feed.Slug, episode.DownloadedFileName!);
            if (!DownloadFileStore.TryDelete(filePath, logger))
            {
                // Locked (rare here -- the newest N are the likely-playing ones and they
                // survive the cap). Leave the record so the next sweep retries.
                continue;
            }

            episode.IsDownloaded = false;
            episode.DownloadedFileName = null;
            progressTracker.Clear(episode.Id);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes RSS files for episodes already flagged played whose download is still on
    /// disk -- i.e. a mark-as-played whose delete was deferred because the file was in
    /// use. Retries the delete and clears the DB pointer once it succeeds; files still
    /// locked are left for the next sweep. Restart-safe: the work list is derived purely
    /// from DB state (IsPlayed + IsDownloaded), so a crash mid-playback recovers on the
    /// next refresh/shutdown.
    /// </summary>
    private async Task RetryDeferredDeletionsAsync(AppDbContext db, Feed feed, CancellationToken ct)
    {
        var playedOnDisk = await db.Episodes
            .Where(e => e.FeedId == feed.Id && e.IsPlayed && e.IsDownloaded && e.DownloadedFileName != null)
            .ToListAsync(ct);

        if (playedOnDisk.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var episode in playedOnDisk)
        {
            var filePath = DownloadFileStore.PathFor(feed.Slug, episode.DownloadedFileName!);
            if (!DownloadFileStore.TryDelete(filePath, logger))
            {
                continue;
            }

            episode.IsDownloaded = false;
            episode.DownloadedFileName = null;
            progressTracker.Clear(episode.Id);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
