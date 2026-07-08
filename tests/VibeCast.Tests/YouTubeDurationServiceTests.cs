using System.Net;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeCast.Data;
using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

// The duration backfill scrapes watch pages concurrently and then applies the results
// through one context; this pins that every scraped duration still lands on the right row,
// and that unaired premieres (lengthSeconds=0) are treated as "unknown, retry later"
// rather than a real zero-length episode.
public class YouTubeDurationServiceTests
{
    [Fact]
    public async Task BackfillAsync_WritesScrapedDurationForEveryVideo()
    {
        using var factory = new TestDbContextFactory();
        var durationsByVideo = new Dictionary<string, int> { ["aaa"] = 61, ["bbb"] = 125, ["ccc"] = 3600 };

        var episodes = await SeedYouTubeEpisodesAsync(factory, durationsByVideo.Keys);

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var videoId = HttpUtility.ParseQueryString(request.RequestUri!.Query)["v"]!;
            var html = $"<html>...\"lengthSeconds\":\"{durationsByVideo[videoId]}\"...</html>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        }));

        var service = new YouTubeDurationService(httpClient, factory, NullLogger<YouTubeDurationService>.Instance);
        await service.BackfillAsync(episodes, CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var byVideo = await db.Episodes.ToDictionaryAsync(e => e.YouTubeVideoId!, e => e.DurationSeconds);
        Assert.Equal(61, byVideo["aaa"]);
        Assert.Equal(125, byVideo["bbb"]);
        Assert.Equal(3600, byVideo["ccc"]);
    }

    [Fact]
    public async Task BackfillAsync_LeavesDurationNull_WhenScrapeHasNoLength()
    {
        using var factory = new TestDbContextFactory();
        var episodes = await SeedYouTubeEpisodesAsync(factory, ["aaa"]);

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>no length here</html>") }));

        var service = new YouTubeDurationService(httpClient, factory, NullLogger<YouTubeDurationService>.Instance);
        await service.BackfillAsync(episodes, CancellationToken.None);

        await using var db = factory.CreateDbContext();
        Assert.Null((await db.Episodes.SingleAsync()).DurationSeconds);
    }

    [Fact]
    public async Task BackfillAsync_TreatsUnairedPremiereAsUpcoming_NotZeroDuration()
    {
        using var factory = new TestDbContextFactory();
        var episodes = await SeedYouTubeEpisodesAsync(factory, ["upc"]);

        // An unaired premiere reports lengthSeconds 0 plus a scheduledStartTime epoch.
        const long epoch = 1_784_073_600; // 2026-07-14T14:40:00Z
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<html>\"lengthSeconds\":\"0\" \"scheduledStartTime\":\"{epoch}\"</html>"),
            }));

        var service = new YouTubeDurationService(httpClient, factory, NullLogger<YouTubeDurationService>.Instance);
        await service.BackfillAsync(episodes, CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var episode = await db.Episodes.SingleAsync();
        Assert.Null(episode.DurationSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime, episode.ScheduledStartUtc);
        Assert.Equal(DateTimeKind.Utc, episode.ScheduledStartUtc!.Value.Kind);
    }

    [Fact]
    public async Task BackfillFeedAsync_HealsAiredPremiere_SettingDurationAndClearingSchedule()
    {
        using var factory = new TestDbContextFactory();

        // Start from the state a prior refresh left: no duration, scheduled-start set.
        int episodeId;
        await using (var seed = factory.CreateDbContext())
        {
            var feed = NewYouTubeFeed();
            var episode = new Episode
            {
                DedupKey = "yt:aired",
                Title = "aired",
                YouTubeVideoId = "aired",
                Feed = feed,
                PublishedAtUtc = DateTime.UtcNow.AddDays(-1),
                ScheduledStartUtc = DateTime.UtcNow.AddDays(-1),
            };
            seed.Episodes.Add(episode);
            await seed.SaveChangesAsync();
            episodeId = episode.Id;
        }

        // Now the video has aired: real length, no scheduledStartTime on the page.
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>\"lengthSeconds\":\"6770\"</html>") }));

        var service = new YouTubeDurationService(httpClient, factory, NullLogger<YouTubeDurationService>.Instance);
        await service.BackfillFeedAsync(await FeedIdOfAsync(factory, episodeId), CancellationToken.None);

        await using var db = factory.CreateDbContext();
        var healed = await db.Episodes.SingleAsync();
        Assert.Equal(6770, healed.DurationSeconds);
        Assert.Null(healed.ScheduledStartUtc);
    }

    [Fact]
    public async Task BackfillFeedAsync_RescrapesRowsLeftAtZeroByOlderBuild()
    {
        using var factory = new TestDbContextFactory();

        int episodeId;
        await using (var seed = factory.CreateDbContext())
        {
            var feed = NewYouTubeFeed();
            var episode = new Episode
            {
                DedupKey = "yt:zero",
                Title = "zero",
                YouTubeVideoId = "zero",
                Feed = feed,
                PublishedAtUtc = DateTime.UtcNow.AddDays(-2),
                DurationSeconds = 0, // legacy: an unaired premiere stored as a bogus 0
            };
            seed.Episodes.Add(episode);
            await seed.SaveChangesAsync();
            episodeId = episode.Id;
        }

        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>\"lengthSeconds\":\"1234\"</html>") }));

        var service = new YouTubeDurationService(httpClient, factory, NullLogger<YouTubeDurationService>.Instance);
        await service.BackfillFeedAsync(await FeedIdOfAsync(factory, episodeId), CancellationToken.None);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1234, (await db.Episodes.SingleAsync()).DurationSeconds);
    }

    [Fact]
    public async Task BackfillFeedAsync_SkipsEpisodesOlderThanRescrapeWindow()
    {
        using var factory = new TestDbContextFactory();

        int episodeId;
        await using (var seed = factory.CreateDbContext())
        {
            var feed = NewYouTubeFeed();
            var episode = new Episode
            {
                DedupKey = "yt:old",
                Title = "old",
                YouTubeVideoId = "old",
                Feed = feed,
                PublishedAtUtc = DateTime.UtcNow.AddDays(-120), // well past the 45-day window
            };
            seed.Episodes.Add(episode);
            await seed.SaveChangesAsync();
            episodeId = episode.Id;
        }

        var scrapeCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref scrapeCount);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>\"lengthSeconds\":\"99\"</html>") };
        }));

        var service = new YouTubeDurationService(httpClient, factory, NullLogger<YouTubeDurationService>.Instance);
        await service.BackfillFeedAsync(await FeedIdOfAsync(factory, episodeId), CancellationToken.None);

        Assert.Equal(0, scrapeCount);
        await using var db = factory.CreateDbContext();
        Assert.Null((await db.Episodes.SingleAsync()).DurationSeconds);
    }

    private static async Task<int> FeedIdOfAsync(TestDbContextFactory factory, int episodeId)
    {
        await using var db = factory.CreateDbContext();
        return await db.Episodes.Where(e => e.Id == episodeId).Select(e => e.FeedId).SingleAsync();
    }

    private static Feed NewYouTubeFeed() => new()
    {
        OriginalUrl = "https://youtube.com/feed",
        FeedUrl = "https://youtube.com/feed",
        Slug = "yt-" + Guid.NewGuid().ToString("N"),
        Type = FeedType.YouTube,
        DateAddedUtc = DateTime.UtcNow,
    };

    private static async Task<List<Episode>> SeedYouTubeEpisodesAsync(TestDbContextFactory factory, IEnumerable<string> videoIds)
    {
        await using var db = factory.CreateDbContext();
        var feed = NewYouTubeFeed();

        var episodes = videoIds
            .Select(id => new Episode { DedupKey = $"yt:{id}", Title = id, YouTubeVideoId = id, Feed = feed })
            .ToList();

        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync();
        return episodes;
    }
}
