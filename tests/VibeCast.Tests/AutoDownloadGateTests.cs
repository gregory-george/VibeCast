using VibeCast.Data;
using VibeCast.Downloads;
using Xunit;

namespace VibeCast.Tests;

// AutoDownloadGate is the single source of truth for "should this be auto-downloaded",
// used both at ingest and by the startup resume sweep, so it's worth pinning tightly.
public class AutoDownloadGateTests
{
    private static Feed NewFeed(bool autoDownload = true, int? maxAgeDays = 90) => new()
    {
        OriginalUrl = "https://example/feed",
        FeedUrl = "https://example/feed",
        Slug = "feed",
        Type = FeedType.Rss,
        AutoDownloadEnabled = autoDownload,
        AutoDownloadMaxAgeDays = maxAgeDays,
    };

    private static Episode NewEpisode(string? enclosureUrl = "https://cdn/ep.mp3", DateTime? publishedUtc = null) => new()
    {
        DedupKey = "guid:x",
        Title = "Ep",
        EnclosureUrl = enclosureUrl,
        PublishedAtUtc = publishedUtc ?? DateTime.UtcNow,
    };

    [Fact]
    public void FreshRssEpisode_WithinCutoff_IsDownloaded()
    {
        Assert.True(AutoDownloadGate.ShouldAutoDownload(NewFeed(), NewEpisode()));
    }

    [Fact]
    public void YouTubeEpisode_NoEnclosure_IsNeverDownloaded()
    {
        Assert.False(AutoDownloadGate.ShouldAutoDownload(NewFeed(), NewEpisode(enclosureUrl: null)));
    }

    [Fact]
    public void PlayedEpisode_IsSkipped()
    {
        var episode = NewEpisode();
        episode.IsPlayed = true;
        Assert.False(AutoDownloadGate.ShouldAutoDownload(NewFeed(), episode));
    }

    [Fact]
    public void ArchivedEpisode_IsSkipped()
    {
        // Pre-archived back-catalog items must not be re-queued by ingest or the sweep.
        var episode = NewEpisode();
        episode.IsArchived = true;
        Assert.False(AutoDownloadGate.ShouldAutoDownload(NewFeed(), episode));
    }

    [Fact]
    public void FeedWithAutoDownloadDisabled_IsSkipped()
    {
        Assert.False(AutoDownloadGate.ShouldAutoDownload(NewFeed(autoDownload: false), NewEpisode()));
    }

    [Fact]
    public void EpisodeOlderThanCutoff_IsSkipped()
    {
        var old = NewEpisode(publishedUtc: DateTime.UtcNow.AddDays(-91));
        Assert.False(AutoDownloadGate.ShouldAutoDownload(NewFeed(maxAgeDays: 90), old));
    }

    [Fact]
    public void EpisodeJustInsideCutoff_IsDownloaded()
    {
        var recent = NewEpisode(publishedUtc: DateTime.UtcNow.AddDays(-89));
        Assert.True(AutoDownloadGate.ShouldAutoDownload(NewFeed(maxAgeDays: 90), recent));
    }

    [Fact]
    public void NullMaxAge_MeansNoAgeLimit()
    {
        var ancient = NewEpisode(publishedUtc: DateTime.UtcNow.AddYears(-5));
        Assert.True(AutoDownloadGate.ShouldAutoDownload(NewFeed(maxAgeDays: null), ancient));
    }
}
