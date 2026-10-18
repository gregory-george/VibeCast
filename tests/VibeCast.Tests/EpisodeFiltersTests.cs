using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Episodes;
using Xunit;

namespace VibeCast.Tests;

// Library-page episode filters: boxes in a group OR together, groups AND together, and
// the defaults reproduce the plain active list.
public class EpisodeFiltersTests
{
    [Fact]
    public async Task Defaults_ShowEveryActiveEpisode_AndNoArchived()
    {
        using var factory = await SeedAsync();

        var titles = await QueryAsync(factory, new EpisodeFilters());

        Assert.Equal(["Downloaded", "Not downloaded", "Short", "Streamed long", "Upcoming", "In progress"], titles);
    }

    [Fact]
    public async Task Status_ArchivedAndUnplayedCombine()
    {
        using var factory = await SeedAsync();

        Assert.Equal(["Archived"], await QueryAsync(factory, new EpisodeFilters { Unplayed = false, Archived = true }));
        Assert.Equal(7, (await QueryAsync(factory, new EpisodeFilters { Archived = true })).Count);
        Assert.Empty(await QueryAsync(factory, new EpisodeFilters { Unplayed = false }));
    }

    [Fact]
    public async Task Type_LongsIncludePodcasts_ShortsAreYouTubeOnly()
    {
        using var factory = await SeedAsync();

        Assert.Equal(["Short"], await QueryAsync(factory, new EpisodeFilters { Longs = false }));
        Assert.DoesNotContain("Short", await QueryAsync(factory, new EpisodeFilters { Shorts = false }));
        Assert.Contains("Downloaded", await QueryAsync(factory, new EpisodeFilters { Shorts = false }));
    }

    [Fact]
    public async Task Source_SplitsRssByDiskAndYouTubeByAirState()
    {
        using var factory = await SeedAsync();
        var none = new EpisodeFilters { Downloaded = false, NotDownloaded = false, Streamed = false, Upcoming = false };

        Assert.Empty(await QueryAsync(factory, none));
        Assert.Equal(["Downloaded"], await QueryAsync(factory, new EpisodeFilters { NotDownloaded = false, Streamed = false, Upcoming = false }));
        Assert.Equal(["Upcoming"], await QueryAsync(factory, new EpisodeFilters { Downloaded = false, NotDownloaded = false, Streamed = false }));
        Assert.Equal(["Short", "Streamed long"], await QueryAsync(factory, new EpisodeFilters { Downloaded = false, NotDownloaded = false, Upcoming = false }));
    }

    [Fact]
    public async Task InProgressOnly_KeepsUnplayedEpisodesWithAResumePosition()
    {
        using var factory = await SeedAsync();

        Assert.Equal(["In progress"], await QueryAsync(factory, new EpisodeFilters { Archived = true, InProgressOnly = true }));
    }

    [Fact]
    public async Task TitleSearch_IsCaseInsensitive_AndTreatsWildcardsLiterally()
    {
        using var factory = await SeedAsync();

        Assert.Equal(["Streamed long"], await QueryAsync(factory, new EpisodeFilters(), "STREAMED"));
        Assert.Empty(await QueryAsync(factory, new EpisodeFilters(), "%"));
    }

    [Fact]
    public void MatchesSource_UsesDiskStateForRss()
    {
        var onlyNotDownloaded = new EpisodeFilters { Downloaded = false };

        // Flagged downloaded but the file is gone: counts as not downloaded.
        Assert.True(onlyNotDownloaded.MatchesSource(isYouTube: false, isUpcoming: false, isOnDisk: false));
        Assert.False(onlyNotDownloaded.MatchesSource(isYouTube: false, isUpcoming: false, isOnDisk: true));
    }

    [Fact]
    public void ChangedCountAndReset_TrackDeparturesFromDefaults()
    {
        var filters = new EpisodeFilters { Archived = true, Shorts = false };

        Assert.Equal(2, filters.ChangedCount());
        filters.Reset();
        Assert.Equal(0, filters.ChangedCount());
    }

    [Fact]
    public void Config_RoundTripsFilters_AndDefaultsWhenMissingOrNull()
    {
        var config = new AppConfig { EpisodeFilters = { Archived = true, Streamed = false } };
        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;

        Assert.True(restored.EpisodeFilters.Archived);
        Assert.False(restored.EpisodeFilters.Streamed);
        Assert.Equal(0, JsonSerializer.Deserialize<AppConfig>("{}")!.EpisodeFilters.ChangedCount());
        Assert.NotNull(JsonSerializer.Deserialize<AppConfig>("""{"EpisodeFilters":null}""")!.EpisodeFilters);
    }

    private static async Task<List<string>> QueryAsync(TestDbContextFactory factory, EpisodeFilters filters, string? search = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await filters.Apply(db.Episodes, search).OrderBy(e => e.Id).Select(e => e.Title).ToListAsync();
    }

    private static async Task<TestDbContextFactory> SeedAsync()
    {
        var factory = new TestDbContextFactory();
        await using var db = await factory.CreateDbContextAsync();

        var rss = new Feed
        {
            OriginalUrl = "https://example/feed",
            FeedUrl = "https://example/feed",
            Slug = "rss",
            Title = "Podcast",
            Type = FeedType.Rss,
            DateAddedUtc = DateTime.UtcNow,
        };
        var yt = new Feed
        {
            OriginalUrl = "https://youtube.com/channel/UCx",
            FeedUrl = "https://www.youtube.com/feeds/videos.xml?channel_id=UCx",
            Slug = "yt",
            Title = "Channel",
            Type = FeedType.YouTube,
            DateAddedUtc = DateTime.UtcNow,
        };

        var n = 0;
        Episode Rss(string title, bool downloaded = false) => new()
        {
            Feed = rss,
            DedupKey = $"guid:{++n}",
            Title = title,
            EnclosureUrl = $"https://example/{n}.mp3",
            EnclosureMediaType = "audio/mpeg",
            DurationSeconds = 3600,
            IsDownloaded = downloaded,
        };
        Episode YouTube(string title, int? duration, DateTime? scheduled = null) => new()
        {
            Feed = yt,
            DedupKey = $"yt:{++n}",
            Title = title,
            YouTubeVideoId = $"vid{n}",
            DurationSeconds = duration,
            ScheduledStartUtc = scheduled,
        };

        var inProgress = Rss("In progress");
        inProgress.PlaybackPositionSeconds = 120;
        var archived = Rss("Archived");
        archived.IsPlayed = archived.IsArchived = true;

        db.Episodes.AddRange(
            Rss("Downloaded", downloaded: true),
            Rss("Not downloaded"),
            YouTube("Short", 45),
            YouTube("Streamed long", 1800),
            YouTube("Upcoming", null, DateTime.UtcNow.AddDays(1)),
            inProgress,
            archived);
        await db.SaveChangesAsync();
        return factory;
    }
}
