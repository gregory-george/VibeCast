using Microsoft.EntityFrameworkCore;
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
internal sealed class RetentionService(IDbContextFactory<AppDbContext> dbContextFactory, DownloadProgressTracker progressTracker)
{
    public const int DefaultKeepLastCount = 100;

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

        var keepLast = feed.KeepLastCount ?? DefaultKeepLastCount;
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
            var filePath = Path.Combine(AppPaths.DownloadsDirectory, feed.Slug, episode.DownloadedFileName!);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            episode.IsDownloaded = false;
            episode.DownloadedFileName = null;
            progressTracker.Clear(episode.Id);
        }

        await db.SaveChangesAsync(ct);
    }
}
