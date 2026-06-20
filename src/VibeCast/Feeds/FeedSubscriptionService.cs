using System.Xml;
using Microsoft.EntityFrameworkCore;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;

namespace VibeCast.Feeds;

/// <summary>
/// Handles "add by URL": detects YouTube vs. RSS/Atom, resolves the fetchable feed
/// URL, performs the first fetch+parse (so the real title is known before the
/// slug is assigned), then persists the feed and its initial episode batch in one
/// shot. The slug is derived from the resolved title precisely so it never has to
/// be renamed later -- downloaded files live under it.
/// </summary>
internal sealed class FeedSubscriptionService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    YouTubeChannelResolver youTubeChannelResolver,
    FeedFetcher feedFetcher,
    DownloadQueue downloadQueue,
    FeedArtworkService artworkService,
    AppConfig config)
{
    public async Task<AddFeedResult> AddFeedAsync(string inputUrl, CancellationToken ct)
    {
        inputUrl = inputUrl.Trim();
        if (string.IsNullOrWhiteSpace(inputUrl))
        {
            return AddFeedResult.Invalid("Enter a feed or channel URL.");
        }

        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return AddFeedResult.Invalid("Enter a valid http(s) URL.");
        }

        var youTube = await youTubeChannelResolver.TryResolveAsync(inputUrl, ct);
        var type = youTube is not null ? FeedType.YouTube : FeedType.Rss;
        var feedUrl = youTube?.ToFeedUrl(excludeShorts: config.DefaultExcludeShorts) ?? uri.ToString();

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        if (await db.Feeds.AnyAsync(f => f.FeedUrl == feedUrl, ct))
        {
            return AddFeedResult.Invalid("This feed is already subscribed.");
        }

        ParsedFeed parsed;
        try
        {
            parsed = await feedFetcher.FetchAndParseAsync(feedUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            return AddFeedResult.Invalid($"Could not fetch the feed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return AddFeedResult.Invalid("Timed out fetching the feed.");
        }
        catch (XmlException)
        {
            return AddFeedResult.Invalid("The feed could not be parsed as RSS/Atom XML.");
        }

        var title = string.IsNullOrWhiteSpace(parsed.Title) ? uri.Host : parsed.Title;

        var existingSlugs = await db.Feeds.Select(f => f.Slug).ToListAsync(ct);
        var slug = SlugGenerator.Generate(title, existingSlugs);

        var feed = new Feed
        {
            OriginalUrl = inputUrl,
            FeedUrl = feedUrl,
            Type = type,
            Title = title,
            Slug = slug,
            ArtworkUrl = parsed.ArtworkUrl,
            ExcludeShorts = youTube is not null && config.DefaultExcludeShorts,
            AutoDownloadMaxAgeDays = config.DefaultAutoDownloadMaxAgeDays,
            DateAddedUtc = DateTime.UtcNow,
            LastRefreshedUtc = DateTime.UtcNow,
        };

        var seenKeys = new HashSet<string>();
        foreach (var parsedEpisode in parsed.Episodes)
        {
            if (!seenKeys.Add(parsedEpisode.DedupKey))
            {
                // Defensive: guards against a malformed feed repeating the same
                // item, which would otherwise violate the unique (FeedId, DedupKey) index.
                continue;
            }

            feed.Episodes.Add(EpisodeMapper.ToEntity(parsedEpisode));
        }

        if (type == FeedType.Rss)
        {
            foreach (var episode in feed.Episodes.OrderByDescending(e => e.PublishedAtUtc).Skip(config.InitialActiveEpisodeCount))
            {
                episode.IsPlayed = true;
                episode.IsArchived = true;
            }
        }

        db.Feeds.Add(feed);
        await db.SaveChangesAsync(ct);

        await artworkService.EnsureArtworkAsync(feed.Id, ct);

        var feedTitle = feed.Title ?? feed.OriginalUrl;
        foreach (var episode in feed.Episodes)
        {
            if (AutoDownloadGate.ShouldAutoDownload(feed, episode))
            {
                downloadQueue.Enqueue(episode.Id, episode.Title, feedTitle);
            }
        }

        return AddFeedResult.Ok(feed.Id, feed.Episodes.Count);
    }
}
