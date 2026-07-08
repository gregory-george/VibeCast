using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

// Add-feed invariants (CLAUDE.md): on initial RSS subscribe only the newest
// InitialActiveEpisodeCount stay active -- the back catalog is pre-archived so
// auto-download-all can't flood disk on day one; YouTube is never pre-archived.
// Duplicates are rejected before any fetch, and delete is the one deliberate
// full wipe (rows + downloads/<slug> folder).
public class FeedSubscriptionServiceTests
{
    [Fact]
    public async Task AddRssFeed_PreArchivesBackCatalog_AndQueuesOnlyActiveEpisodes()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var config = new AppConfig { InitialActiveEpisodeCount = 2 };
        var service = BuildService(factory, tracker, config, _ => RssResponse(BuildRss(episodeCount: 5)));

        var result = await service.AddFeedAsync("https://example.com/feed.xml", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, result.EpisodeCount);

        await using var db = factory.CreateDbContext();
        var episodes = await db.Episodes.OrderByDescending(e => e.PublishedAtUtc).ToListAsync();
        Assert.Equal(5, episodes.Count);

        foreach (var active in episodes.Take(2))
        {
            Assert.False(active.IsPlayed);
            Assert.False(active.IsArchived);
            Assert.Equal(DownloadStatus.Queued, tracker.Get(active.Id)!.Status);
        }

        foreach (var archived in episodes.Skip(2))
        {
            Assert.True(archived.IsPlayed);
            Assert.True(archived.IsArchived);
            Assert.Null(tracker.Get(archived.Id)); // pre-archived items never auto-download
        }
    }

    [Fact]
    public async Task AddYouTubeFeed_IsNeverPreArchived()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var config = new AppConfig { InitialActiveEpisodeCount = 2 };
        var service = BuildService(factory, tracker, config, request =>
            request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
                ? RssResponse(BuildYouTubeAtom(entryCount: 5))
                : new HttpResponseMessage(HttpStatusCode.NotFound)); // artwork scrape -> none

        var result = await service.AddFeedAsync(
            "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv", CancellationToken.None);

        Assert.True(result.Success);

        await using var db = factory.CreateDbContext();
        var feed = await db.Feeds.Include(f => f.Episodes).SingleAsync();
        Assert.Equal(FeedType.YouTube, feed.Type);
        Assert.Contains("channel_id=UCabcdefghijklmnopqrstuv", feed.FeedUrl);
        Assert.Equal(5, feed.Episodes.Count);
        Assert.All(feed.Episodes, e =>
        {
            Assert.False(e.IsPlayed);   // count exceeds InitialActiveEpisodeCount, but YouTube skips pre-archive
            Assert.False(e.IsArchived);
            Assert.Null(tracker.Get(e.Id)); // and never downloads
        });
    }

    [Fact]
    public async Task AddYouTubeFeed_DefaultExcludeShorts_UsesLongFormPlaylistFeed()
    {
        using var factory = new TestDbContextFactory();
        var config = new AppConfig { DefaultExcludeShorts = true };
        var service = BuildService(factory, new DownloadProgressTracker(), config, request =>
            request.RequestUri!.AbsolutePath.Contains("feeds/videos.xml")
                ? RssResponse(BuildYouTubeAtom(entryCount: 1))
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await service.AddFeedAsync(
            "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv", CancellationToken.None);

        Assert.True(result.Success);

        await using var db = factory.CreateDbContext();
        var feed = await db.Feeds.SingleAsync();
        Assert.Contains("playlist_id=UULFabcdefghijklmnopqrstuv", feed.FeedUrl); // UC -> UULF swap
        Assert.True(feed.ExcludeShorts);
    }

    [Fact]
    public async Task DuplicateFeed_IsRejected()
    {
        using var factory = new TestDbContextFactory();
        var service = BuildService(factory, new DownloadProgressTracker(), new AppConfig(),
            _ => RssResponse(BuildRss(episodeCount: 1)));

        var first = await service.AddFeedAsync("https://example.com/feed.xml", CancellationToken.None);
        var second = await service.AddFeedAsync("https://example.com/feed.xml", CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("already subscribed", second.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/feed.xml")]
    public async Task InvalidInput_IsRejectedWithoutFetching(string input)
    {
        using var factory = new TestDbContextFactory();
        var service = BuildService(factory, new DownloadProgressTracker(), new AppConfig(),
            _ => throw new InvalidOperationException("network must not be touched"));

        var result = await service.AddFeedAsync(input, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task UnparseableFeed_IsRejectedWithClearError()
    {
        using var factory = new TestDbContextFactory();
        var service = BuildService(factory, new DownloadProgressTracker(), new AppConfig(),
            _ => RssResponse("this is not xml at all"));

        var result = await service.AddFeedAsync("https://example.com/feed.xml", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("could not be parsed", result.Error);
    }

    [Fact]
    public async Task DeleteFeed_WipesRowsAndDownloadsFolder()
    {
        using var factory = new TestDbContextFactory();
        var service = BuildService(factory, new DownloadProgressTracker(), new AppConfig(),
            _ => RssResponse(BuildRss(episodeCount: 2)));

        var added = await service.AddFeedAsync("https://example.com/feed.xml", CancellationToken.None);
        Assert.True(added.Success);

        string slug;
        await using (var db = factory.CreateDbContext())
        {
            slug = (await db.Feeds.SingleAsync()).Slug;
        }

        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, slug);
        Directory.CreateDirectory(feedDir);
        await File.WriteAllTextAsync(Path.Combine(feedDir, "ep.mp3"), "audio");

        await service.DeleteFeedAsync(added.FeedId!.Value, CancellationToken.None);

        await using (var verify = factory.CreateDbContext())
        {
            Assert.Empty(await verify.Feeds.ToListAsync());
            Assert.Empty(await verify.Episodes.ToListAsync()); // episodes cascade
        }

        Assert.False(Directory.Exists(feedDir));
    }

    private static FeedSubscriptionService BuildService(
        TestDbContextFactory factory,
        DownloadProgressTracker tracker,
        AppConfig config,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responder));
        return new FeedSubscriptionService(
            factory,
            new YouTubeChannelResolver(httpClient),
            new FeedFetcher(httpClient),
            new DownloadQueue(tracker),
            new DownloadCancellationRegistry(tracker),
            tracker,
            new FeedArtworkService(httpClient, factory, NullLogger<FeedArtworkService>.Instance),
            config);
    }

    private static HttpResponseMessage RssResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>RSS feed with recent pubdates (inside the 90-day auto-download cutoff), newest first.</summary>
    private static string BuildRss(int episodeCount)
    {
        var items = new StringBuilder();
        for (var i = 0; i < episodeCount; i++)
        {
            var pubDate = DateTime.UtcNow.AddDays(-i).ToString("R", CultureInfo.InvariantCulture);
            items.Append($"""
                <item>
                  <guid>ep-{i}</guid>
                  <title>Episode {i}</title>
                  <pubDate>{pubDate}</pubDate>
                  <enclosure url="https://cdn.example/ep{i}.mp3" type="audio/mpeg" length="1" />
                </item>
                """);
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel><title>Subscribe Test Feed</title>{items}</channel></rss>
            """;
    }

    private static string BuildYouTubeAtom(int entryCount)
    {
        var entries = new StringBuilder();
        for (var i = 0; i < entryCount; i++)
        {
            var published = DateTime.UtcNow.AddDays(-i).ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
            entries.Append($"""
                <entry>
                  <id>yt:video:VID{i}</id>
                  <yt:videoId>VID{i}</yt:videoId>
                  <title>Video {i}</title>
                  <published>{published}</published>
                </entry>
                """);
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:yt="http://www.youtube.com/xml/schemas/2015">
              <title>Test Channel</title>{entries}
            </feed>
            """;
    }
}
