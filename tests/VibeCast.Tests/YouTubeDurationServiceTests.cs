using System.Net;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeCast.Data;
using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

// The duration backfill scrapes watch pages concurrently and then applies the results
// through one context; this pins that every scraped duration still lands on the right row.
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

    private static async Task<List<Episode>> SeedYouTubeEpisodesAsync(TestDbContextFactory factory, IEnumerable<string> videoIds)
    {
        await using var db = factory.CreateDbContext();
        var feed = new Feed
        {
            OriginalUrl = "https://youtube.com/feed",
            FeedUrl = "https://youtube.com/feed",
            Slug = "yt-" + Guid.NewGuid().ToString("N"),
            Type = FeedType.YouTube,
            DateAddedUtc = DateTime.UtcNow,
        };

        var episodes = videoIds
            .Select(id => new Episode { DedupKey = $"yt:{id}", Title = id, YouTubeVideoId = id, Feed = feed })
            .ToList();

        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync();
        return episodes;
    }
}
