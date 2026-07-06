using System.Net;
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
    FeedArtworkService artworkService,
    YouTubeChannelResolver youTubeChannelResolver,
    YouTubeDurationService youTubeDurationService,
    ILogger<FeedRefreshService> logger,
    // Backoff between fetch retries. Injectable so tests don't wait real seconds; production
    // (and DI, which can't supply it) falls back to the exponential default.
    Func<int, TimeSpan>? retryDelayProvider = null)
{
    // Refresh feeds concurrently but bounded, so a large subscription list isn't a long
    // sequential wait. The cap also limits simultaneous network fetches and SQLite writers
    // -- WAL is single-writer, and the connection's 30s busy timeout absorbs the contention.
    private const int MaxConcurrentRefreshes = 4;

    // A transient network blip shouldn't permanently mark a feed as failed. Retry the fetch
    // a few times with backoff; only genuinely transient errors are retried (see IsTransient).
    private const int MaxFetchAttempts = 3;

    private readonly Func<int, TimeSpan> retryDelay = retryDelayProvider ?? DefaultRetryDelay;

    private static TimeSpan DefaultRetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

    public async Task RefreshAllAsync(CancellationToken ct)
    {
        List<int> feedIds;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            feedIds = await db.Feeds.Select(f => f.Id).ToListAsync(ct);
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRefreshes,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(feedIds, options, async (feedId, token) =>
        {
            try
            {
                // RefreshFeedAsync already records fetch/parse failures per feed (each on
                // its own DbContext from the factory); this guard just stops an unexpected
                // error in one feed from aborting the whole batch.
                await RefreshFeedAsync(feedId, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error refreshing feed {FeedId}", feedId);
            }
        });
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
            parsed = await FetchWithRetriesAsync(feed, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown / navigation-away cancelled the refresh -- not a feed-health problem,
            // so let it propagate rather than recording a spurious LastRefreshError.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Refresh failed for feed {FeedId} ({Url}) after {Attempts} attempt(s)", feed.Id, feed.FeedUrl, MaxFetchAttempts);
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

        if (parsed.ArtworkUrl is not null && parsed.ArtworkUrl != feed.ArtworkUrl)
        {
            feed.ArtworkUrl = parsed.ArtworkUrl;
        }
        else if (feed.Type == FeedType.YouTube && feed.ArtworkUrl is null)
        {
            // Backfill for feeds subscribed before artwork scraping existed.
            // videos.xml never carries artwork itself, so this is the only path that sets it.
            feed.ArtworkUrl = await youTubeChannelResolver.TryGetArtworkUrlAsync(
                YouTubeChannelResolution.FromRawFeedUrl(feed.FeedUrl), ct);
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

        await artworkService.EnsureArtworkAsync(feed.Id, ct);

        if (feed.Type == FeedType.YouTube && newEpisodes.Count > 0)
        {
            // videos.xml carries no duration; backfill only the newly-added
            // episodes by scraping each watch page (best-effort, never throws).
            await youTubeDurationService.BackfillAsync(newEpisodes, ct);
        }

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

    /// <summary>
    /// Fetches the feed, retrying transient failures with backoff. Non-transient errors
    /// (permanent 4xx, malformed XML) and the final exhausted attempt propagate to the
    /// caller, which records them as the feed's LastRefreshError.
    /// </summary>
    private async Task<ParsedFeed> FetchWithRetriesAsync(Feed feed, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await feedFetcher.FetchAndParseAsync(feed.FeedUrl, ct);
            }
            catch (Exception ex) when (attempt < MaxFetchAttempts && IsTransient(ex, ct))
            {
                var delay = retryDelay(attempt);
                logger.LogInformation(
                    "Transient failure refreshing feed {FeedId} ({Url}) on attempt {Attempt}/{Max}; retrying in {Delay:0.#}s: {Message}",
                    feed.Id, feed.FeedUrl, attempt, MaxFetchAttempts, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            // Our own cancellation (shutdown/navigation), not a flaky server -- don't retry.
            return false;
        }

        return ex switch
        {
            // With our token ruled out above, a TaskCanceledException is an HttpClient timeout.
            TaskCanceledException => true,
            HttpRequestException http => IsRetryableStatus(http.StatusCode),
            // XmlException (malformed feed), oversize body, etc. won't fix themselves on retry.
            _ => false,
        };
    }

    private static bool IsRetryableStatus(HttpStatusCode? status) => status switch
    {
        null => true,                                   // transport/DNS failure -- no response at all
        HttpStatusCode.RequestTimeout => true,          // 408
        HttpStatusCode.TooManyRequests => true,         // 429
        >= HttpStatusCode.InternalServerError => true,  // 5xx -- server-side, likely transient
        _ => false,                                     // other 4xx (404/410/403...) -- permanent
    };

    private static async Task<FeedRefreshResult> RecordFailureAsync(AppDbContext db, Feed feed, CancellationToken ct, string error)
    {
        feed.LastRefreshedUtc = DateTime.UtcNow;
        feed.LastRefreshError = error;
        await db.SaveChangesAsync(ct);
        return FeedRefreshResult.Failed(error);
    }
}
