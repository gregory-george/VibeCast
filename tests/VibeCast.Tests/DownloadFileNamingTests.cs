using VibeCast.Data;
using VibeCast.Downloads;
using Xunit;

namespace VibeCast.Tests;

public class DownloadFileNamingTests
{
    private static Episode NewEpisode(string title, string? mediaType, int id = 7) => new()
    {
        Id = id,
        DedupKey = "guid:x",
        Title = title,
        PublishedAtUtc = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
        EnclosureUrl = "https://cdn.example/ep",
        EnclosureMediaType = mediaType,
    };

    [Fact]
    public void BuildFileName_UsesDatePrefix_TitleSlug_IdAndMediaExtension()
    {
        var name = DownloadFileNaming.BuildFileName(NewEpisode("My Great Episode", "audio/mpeg"));
        Assert.Equal("2026-03-04-my-great-episode-7.mp3", name);
    }

    [Fact]
    public void BuildFileName_ExtensionComesFromMediaType_NotAnyUrlName()
    {
        // Unknown media type must never yield an executable extension, regardless of URL.
        var name = DownloadFileNaming.BuildFileName(NewEpisode("Episode", "application/x-msdownload"));
        Assert.EndsWith(".bin", name);
    }

    [Fact]
    public void BuildFileName_EmptyTitleSlug_FallsBackToEpisode()
    {
        var name = DownloadFileNaming.BuildFileName(NewEpisode("!!!", "audio/mpeg", id: 12));
        Assert.Equal("2026-03-04-episode-12.mp3", name);
    }

    [Fact]
    public void BuildFileName_AppendsIdForUniqueness()
    {
        var a = DownloadFileNaming.BuildFileName(NewEpisode("Same Title", "audio/mpeg", id: 1));
        var b = DownloadFileNaming.BuildFileName(NewEpisode("Same Title", "audio/mpeg", id: 2));
        Assert.NotEqual(a, b);
        Assert.EndsWith("-1.mp3", a);
        Assert.EndsWith("-2.mp3", b);
    }
}
