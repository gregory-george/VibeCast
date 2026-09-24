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

// Refresh updates metadata on unplayed episodes still in the feed window, but never
// touches played/archived ones.
public class FeedRefreshMetadataTests
{
    private static string Rss(string suffix, string pubDate = "<pubDate>Mon, 02 Jan 2006 15:04:05 GMT</pubDate>") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>Metadata Test Feed</title>
            <item>
              <guid>ep-unplayed</guid>
              <title>Unplayed {suffix}</title>
              {pubDate}
              <description>Notes {suffix}</description>
              <itunes:image href="https://cdn.example/art-{suffix}.jpg" />
              <itunes:duration>{(suffix == "v1" ? "600" : "900")}</itunes:duration>
              <enclosure url="https://cdn.example/unplayed-{suffix}.mp3" type="audio/mpeg" length="1" />
            </item>
            <item>
              <guid>ep-played</guid>
              <title>Played {suffix}</title>
              {pubDate}
              <description>Notes {suffix}</description>
              <enclosure url="https://cdn.example/played-{suffix}.mp3" type="audio/mpeg" length="1" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public async Task RefreshFeedAsync_UpdatesUnplayedMetadata_LeavesPlayedUntouched()
    {
        using var factory = new TestDbContextFactory();
        var feedId = await SeedRssFeedAsync(factory);

        var body = Rss("v1");
        var service = BuildService(factory, () => body);
        Assert.True((await service.RefreshFeedAsync(feedId, CancellationToken.None)).Success);

        await using (var db = factory.CreateDbContext())
        {
            var played = await db.Episodes.SingleAsync(e => e.DedupKey == "guid:ep-played");
            played.IsPlayed = true;
            played.IsArchived = true;
            await db.SaveChangesAsync();
        }

        body = Rss("v2");
        var result = await service.RefreshFeedAsync(feedId, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(0, result.AddedCount);

        await using var verify = factory.CreateDbContext();
        var unplayed = await verify.Episodes.SingleAsync(e => e.DedupKey == "guid:ep-unplayed");
        Assert.Equal("Unplayed v2", unplayed.Title);
        Assert.Equal("Notes v2", unplayed.DescriptionHtml);
        Assert.Equal("https://cdn.example/art-v2.jpg", unplayed.ArtworkUrl);
        Assert.Equal(900, unplayed.DurationSeconds);
        Assert.Equal("https://cdn.example/unplayed-v2.mp3", unplayed.EnclosureUrl);

        var frozen = await verify.Episodes.SingleAsync(e => e.DedupKey == "guid:ep-played");
        Assert.Equal("Played v1", frozen.Title);
        Assert.Equal("Notes v1", frozen.DescriptionHtml);
        Assert.Equal("https://cdn.example/played-v1.mp3", frozen.EnclosureUrl);
    }

    [Fact]
    public async Task RefreshFeedAsync_MissingPubDate_KeepsStoredDate()
    {
        using var factory = new TestDbContextFactory();
        var feedId = await SeedRssFeedAsync(factory);

        var body = Rss("v1");
        var service = BuildService(factory, () => body);
        await service.RefreshFeedAsync(feedId, CancellationToken.None);

        body = Rss("v2", pubDate: "");
        await service.RefreshFeedAsync(feedId, CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var unplayed = await db.Episodes.SingleAsync(e => e.DedupKey == "guid:ep-unplayed");
        Assert.Equal("Unplayed v2", unplayed.Title);
        Assert.Equal(new DateTime(2006, 1, 2, 15, 4, 5, DateTimeKind.Utc), unplayed.PublishedAtUtc);
    }

    [Fact]
    public void ApplyMetadata_DownloadedEpisode_KeepsEnclosurePinnedToFileOnDisk()
    {
        var episode = new Episode
        {
            DedupKey = "guid:x",
            Title = "Old",
            EnclosureUrl = "https://cdn.example/old.mp3",
            EnclosureMediaType = "audio/mpeg",
            IsDownloaded = true,
            DownloadedFileName = "old.mp3",
        };
        var parsed = new ParsedEpisode(
            "guid:x", "New", DateTimeOffset.UtcNow, HasPublishedDate: true, "notes", null, null,
            "https://cdn.example/new.m4a", "audio/mp4", null);

        FeedRefreshService.ApplyMetadata(episode, parsed, "slug-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("New", episode.Title);
        Assert.Equal("https://cdn.example/old.mp3", episode.EnclosureUrl);
        Assert.Equal("audio/mpeg", episode.EnclosureMediaType);
    }

    private static async Task<int> SeedRssFeedAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var feed = new Feed
        {
            OriginalUrl = "https://example/feed.xml",
            FeedUrl = "https://example/feed.xml",
            Slug = "meta-" + Guid.NewGuid().ToString("N"),
            Type = FeedType.Rss,
            DateAddedUtc = DateTime.UtcNow,
            // Keep the refresh from queueing downloads; only metadata is under test.
            AutoDownloadEnabled = false,
        };
        db.Feeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.Id;
    }

    private static FeedRefreshService BuildService(TestDbContextFactory factory, Func<string> feedBody)
    {
        var tracker = new DownloadProgressTracker();
        var config = new AppConfig();
        var feedFetcher = new FeedFetcher(new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feedBody()) })));
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
            retryDelayProvider: _ => TimeSpan.Zero);
    }
}
