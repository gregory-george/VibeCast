using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Retention;

namespace VibeCast.Feeds;

/// <summary>
/// Purely additive refresh: pulls the current feed contents, adds episodes not
/// already stored (by DedupKey), and never removes ones that aged out of the
/// feed's window. The DB is the source of truth, the feed is discovery only.
/// </summary>
internal sealed class FeedRefreshService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    FeedFetcher feedFetcher,
    DownloadQueue downloadQueue,
    RetentionService retentionService,
    ILogger<FeedRefreshService> logger)
{
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var feedIds = await db.Feeds.Select(f => f.Id).ToListAsync(ct);

        foreach (var feedId in feedIds)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshFeedAsync(feedId, ct);
        }
    }

    public async Task<FeedRefreshResult> RefreshFeedAsync(int feedId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var feed = await db.Feeds.FindAsync([feedId], ct);
        if (feed is null)
        {
            return FeedRefreshResult.Failed("Feed not found.");
        }

        ParsedFeed parsed;
        try
        {
            parsed = await feedFetcher.FetchAndParseAsync(feed.FeedUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Refresh failed for feed {FeedId} ({Url})", feed.Id, feed.FeedUrl);
            return await RecordFailureAsync(db, feed, ct, $"Could not fetch the feed: {ex.Message}");
        }
        catch (XmlException ex)
        {
            logger.LogWarning(ex, "Parse failed for feed {FeedId} ({Url})", feed.Id, feed.FeedUrl);
            return await RecordFailureAsync(db, feed, ct, "The feed could not be parsed as RSS/Atom XML.");
        }

        if (string.IsNullOrWhiteSpace(feed.Title) && !string.IsNullOrWhiteSpace(parsed.Title))
        {
            feed.Title = parsed.Title;
        }

        if (feed.ArtworkUrl is null && parsed.ArtworkUrl is not null)
        {
            feed.ArtworkUrl = parsed.ArtworkUrl;
        }

        var existingKeys = await db.Episodes
            .Where(e => e.FeedId == feed.Id)
            .Select(e => e.DedupKey)
            .ToHashSetAsync(ct);

        var newEpisodes = new List<Episode>();
        foreach (var parsedEpisode in parsed.Episodes)
        {
            if (!existingKeys.Add(parsedEpisode.DedupKey))
            {
                continue;
            }

            var episode = EpisodeMapper.ToEntity(parsedEpisode);
            episode.FeedId = feed.Id;
            db.Episodes.Add(episode);
            newEpisodes.Add(episode);
        }

        feed.LastRefreshedUtc = DateTime.UtcNow;
        feed.LastRefreshError = null;
        await db.SaveChangesAsync(ct);

        var feedTitle = feed.Title ?? feed.OriginalUrl;
        foreach (var episode in newEpisodes)
        {
            if (AutoDownloadGate.ShouldAutoDownload(feed, episode))
            {
                downloadQueue.Enqueue(episode.Id, episode.Title, feedTitle);
            }
        }

        // keep-last-N cleanup runs on refresh (CLAUDE.md) -- catches files that aged
        // past the cap even if nothing new downloaded this time.
        await retentionService.EnforceFeedAsync(feed.Id, ct);

        return FeedRefreshResult.Ok(newEpisodes.Count);
    }

    private static async Task<FeedRefreshResult> RecordFailureAsync(AppDbContext db, Feed feed, CancellationToken ct, string error)
    {
        feed.LastRefreshedUtc = DateTime.UtcNow;
        feed.LastRefreshError = error;
        await db.SaveChangesAsync(ct);
        return FeedRefreshResult.Failed(error);
    }
}
