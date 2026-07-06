using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

public class DedupKeyComputerTests
{
    private static readonly DateTimeOffset SamplePubDate = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ForRss_PrefersGuid_OverUrlAndHash()
    {
        var key = DedupKeyComputer.ForRss("abc-123", "https://cdn.example/ep.mp3", "Title", SamplePubDate);
        Assert.Equal("guid:abc-123", key);
    }

    [Fact]
    public void ForRss_TrimsGuidWhitespace()
    {
        var key = DedupKeyComputer.ForRss("  abc-123  ", null, "Title", SamplePubDate);
        Assert.Equal("guid:abc-123", key);
    }

    [Fact]
    public void ForRss_FallsBackToNormalizedUrl_WhenNoGuid()
    {
        var key = DedupKeyComputer.ForRss(null, "https://cdn.example/ep.mp3?token=xyz#frag", "Title", SamplePubDate);
        Assert.Equal("url:https://cdn.example/ep.mp3", key);
    }

    [Fact]
    public void ForRss_UrlKey_IgnoresQueryAndFragment_SoTrackingParamsDontLookNew()
    {
        var withParams = DedupKeyComputer.ForRss(null, "https://cdn.example/ep.mp3?utm=1", "Title", SamplePubDate);
        var without = DedupKeyComputer.ForRss(null, "https://cdn.example/ep.mp3", "Title", SamplePubDate);
        Assert.Equal(without, withParams);
    }

    [Fact]
    public void ForRss_FallsBackToHash_WhenNoGuidOrUrl()
    {
        var key = DedupKeyComputer.ForRss(null, null, "Title", SamplePubDate);
        Assert.StartsWith("hash:", key);
    }

    [Fact]
    public void ForRss_Hash_IsStableForSameTitleAndDate()
    {
        var a = DedupKeyComputer.ForRss(null, null, "Same", SamplePubDate);
        var b = DedupKeyComputer.ForRss(null, null, "Same", SamplePubDate);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ForRss_Hash_DiffersWhenTitleDiffers()
    {
        var a = DedupKeyComputer.ForRss(null, null, "One", SamplePubDate);
        var b = DedupKeyComputer.ForRss(null, null, "Two", SamplePubDate);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ForYouTube_PrefixesVideoId()
    {
        Assert.Equal("yt:dQw4w9WgXcQ", DedupKeyComputer.ForYouTube("dQw4w9WgXcQ"));
    }
}
