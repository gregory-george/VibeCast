using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Feeds;
using VibeCast.Retention;
using Xunit;

namespace VibeCast.Tests;

// Refresh resilience: a transient network blip is retried with backoff before the feed is
// marked failed, but a permanent error (404/410, malformed XML) fails fast without retrying.
public class FeedRefreshRetryTests
{
    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Retry Test Feed</title>
            <item>
              <guid>ep-1</guid>
              <title>Episode 1</title>
              <pubDate>Mon, 02 Jan 2006 15:04:05 GMT</pubDate>
              <enclosure url="https://cdn.example/ep1.mp3" type="audio/mpeg" length="1" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public async Task RefreshFeedAsync_RetriesTransientFailure_ThenSucceeds()
    {
        using var factory = new TestDbContextFactory();
        var feedId = await SeedRssFeedAsync(factory);

        var calls = 0;
        var service = BuildService(factory, _ =>
        {
            calls++;
            return calls <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)   // transient 503 x2
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleRss) };
        });

        var result = await service.RefreshFeedAsync(feedId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, calls); // two failures, then success on the third attempt

        await using var db = factory.CreateDbContext();
        var feed = await db.Feeds.Include(f => f.Episodes).SingleAsync();
        Assert.Null(feed.LastRefreshError);
        Assert.Single(feed.Episodes);
    }

    [Fact]
    public async Task RefreshFeedAsync_GivesUpAfterMaxAttempts_OnPersistentTransientFailure()
    {
        using var factory = new TestDbContextFactory();
        var feedId = await SeedRssFeedAsync(factory);

        var calls = 0;
        var service = BuildService(factory, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        var result = await service.RefreshFeedAsync(feedId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(3, calls); // capped at MaxFetchAttempts
        Assert.NotNull((await SingleFeedAsync(factory)).LastRefreshError);
    }

    [Fact]
    public async Task RefreshFeedAsync_DoesNotRetry_OnPermanentClientError()
    {
        using var factory = new TestDbContextFactory();
        var feedId = await SeedRssFeedAsync(factory);

        var calls = 0;
        var service = BuildService(factory, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await service.RefreshFeedAsync(feedId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, calls); // 404 is permanent -- no point retrying
        Assert.NotNull((await SingleFeedAsync(factory)).LastRefreshError);
    }

    [Fact]
    public async Task RefreshFeedAsync_DoesNotRetry_OnParseError()
    {
        using var factory = new TestDbContextFactory();
        var feedId = await SeedRssFeedAsync(factory);

        var calls = 0;
        var service = BuildService(factory, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<not-a-feed/>") };
        });

        var result = await service.RefreshFeedAsync(feedId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, calls); // malformed feed won't parse on retry either
    }

    private static async Task<int> SeedRssFeedAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var feed = new Feed
        {
            OriginalUrl = "https://example/feed.xml",
            FeedUrl = "https://example/feed.xml",
            Slug = "retry-" + Guid.NewGuid().ToString("N"),
            Type = FeedType.Rss,
            DateAddedUtc = DateTime.UtcNow,
        };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.Id;
    }

    private static async Task<Feed> SingleFeedAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.Feeds.SingleAsync();
    }

    private static FeedRefreshService BuildService(TestDbContextFactory factory, Func<HttpRequestMessage, HttpResponseMessage> feedResponder)
    {
        var tracker = new DownloadProgressTracker();
        var config = new AppConfig();
        var feedFetcher = new FeedFetcher(new HttpClient(new StubHttpMessageHandler(feedResponder)));

        // These aren't reached by the RSS test feed (no artwork, not YouTube), so they just
        // need to be constructable; a 404-returning client keeps them inert if ever touched.
        var inertClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        return new FeedRefreshService(
            factory,
            feedFetcher,
            new DownloadQueue(tracker),
            new RetentionService(factory, tracker, config, NullLogger<RetentionService>.Instance),
            new FeedArtworkService(inertClient, factory, NullLogger<FeedArtworkService>.Instance),
            new YouTubeChannelResolver(inertClient),
            new YouTubeDurationService(inertClient, factory, NullLogger<YouTubeDurationService>.Instance),
            NullLogger<FeedRefreshService>.Instance,
            retryDelayProvider: _ => TimeSpan.Zero); // no real backoff waits in tests
    }
}
