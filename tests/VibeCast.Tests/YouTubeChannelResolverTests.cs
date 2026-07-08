using System.Net;
using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

// YouTube URL-resolution invariants (CLAUDE.md): every accepted input form maps to a
// fetchable videos.xml URL, Exclude-Shorts is the UC -> UULF prefix swap and never
// applies to user playlists (PL...), and handle/custom URLs resolve by scraping the
// channel page (meta tag first, canonical link fallback) with null on any failure.
public class YouTubeChannelResolverTests
{
    private const string ChannelId = "UCabcdefghijklmnopqrstuv";

    /// <summary>Resolver whose HttpClient fails the test if any request is made.</summary>
    private static YouTubeChannelResolver OfflineResolver() =>
        new(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("network must not be touched"))));

    private static YouTubeChannelResolver ScrapingResolver(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHttpMessageHandler(responder)));

    [Fact]
    public async Task RawChannelId_ResolvesWithoutNetwork()
    {
        var resolution = await OfflineResolver().TryResolveAsync(ChannelId, CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.Equal(ChannelId, resolution!.ChannelId);
        Assert.False(resolution.IsCustomPlaylist);
    }

    [Fact]
    public async Task ChannelPathUrl_CarriesTheId_NoScrapeNeeded()
    {
        var resolution = await OfflineResolver().TryResolveAsync(
            $"https://www.youtube.com/channel/{ChannelId}", CancellationToken.None);

        Assert.Equal(ChannelId, resolution!.ChannelId);
    }

    [Fact]
    public async Task PlaylistUrl_IsACustomPlaylist_AndIgnoresExcludeShorts()
    {
        var resolution = await OfflineResolver().TryResolveAsync(
            "https://www.youtube.com/playlist?list=PLabc123", CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.True(resolution!.IsCustomPlaylist);
        // ExcludeShorts must not rewrite a user playlist (PL... != UULF...).
        Assert.Equal(
            "https://www.youtube.com/feeds/videos.xml?playlist_id=PLabc123",
            resolution.ToFeedUrl(excludeShorts: true));
    }

    [Fact]
    public async Task RawFeedUrl_WithChannelId_IsPreservedVerbatim()
    {
        var url = $"https://www.youtube.com/feeds/videos.xml?channel_id={ChannelId}";
        var resolution = await OfflineResolver().TryResolveAsync(url, CancellationToken.None);

        Assert.Equal(url, resolution!.ToFeedUrl(excludeShorts: false));
        Assert.False(resolution.IsCustomPlaylist);
    }

    [Fact]
    public async Task RawFeedUrl_WithPlPlaylist_IsTreatedAsCustomPlaylist()
    {
        var resolution = await OfflineResolver().TryResolveAsync(
            "https://www.youtube.com/feeds/videos.xml?playlist_id=PLxyz789", CancellationToken.None);

        Assert.True(resolution!.IsCustomPlaylist);
    }

    [Fact]
    public async Task RawFeedUrl_WithUulfPlaylist_IsNotACustomPlaylist()
    {
        // UULF... is the channel's own long-form playlist, not a user playlist.
        var resolution = await OfflineResolver().TryResolveAsync(
            "https://www.youtube.com/feeds/videos.xml?playlist_id=UULFabcdefghijklmnopqrstuv",
            CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.False(resolution!.IsCustomPlaylist);
    }

    [Fact]
    public async Task HandleUrl_ScrapesChannelId_FromMetaTag()
    {
        var resolver = ScrapingResolver(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"""<html><meta itemprop="channelId" content="{ChannelId}"></html>"""),
        });

        var resolution = await resolver.TryResolveAsync("https://www.youtube.com/@somehandle", CancellationToken.None);

        Assert.Equal(ChannelId, resolution!.ChannelId);
    }

    [Fact]
    public async Task HandleUrl_FallsBackToCanonicalLink_WhenMetaTagMissing()
    {
        var resolver = ScrapingResolver(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"""<html><link rel="canonical" href="https://www.youtube.com/channel/{ChannelId}"></html>"""),
        });

        var resolution = await resolver.TryResolveAsync("https://www.youtube.com/@somehandle", CancellationToken.None);

        Assert.Equal(ChannelId, resolution!.ChannelId);
    }

    [Fact]
    public async Task ScrapeFailure_ReturnsNull()
    {
        var resolver = ScrapingResolver(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await resolver.TryResolveAsync("https://www.youtube.com/@gone", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://example.com/feed.xml")] // non-YouTube host
    [InlineData("just some text")]
    public async Task NonYouTubeInput_ReturnsNull_WithoutNetwork(string input)
    {
        Assert.Null(await OfflineResolver().TryResolveAsync(input, CancellationToken.None));
    }

    [Fact]
    public void ToFeedUrl_ExcludeShorts_SwapsUcPrefixToUulf()
    {
        var resolution = YouTubeChannelResolution.FromChannelId(ChannelId);

        Assert.Equal(
            "https://www.youtube.com/feeds/videos.xml?playlist_id=UULFabcdefghijklmnopqrstuv",
            resolution.ToFeedUrl(excludeShorts: true));
        Assert.Equal(
            $"https://www.youtube.com/feeds/videos.xml?channel_id={ChannelId}",
            resolution.ToFeedUrl(excludeShorts: false));
    }

    [Fact]
    public async Task ArtworkUrl_IsHtmlDecoded()
    {
        // Playlist thumbnails carry signed query strings whose '&' arrive as '&amp;'.
        var resolver = ScrapingResolver(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """<html><meta property="og:image" content="https://i.ytimg.com/pl.jpg?sqp=abc&amp;rs=def"></html>"""),
        });

        var artwork = await resolver.TryGetArtworkUrlAsync(
            YouTubeChannelResolution.FromChannelId(ChannelId), CancellationToken.None);

        Assert.Equal("https://i.ytimg.com/pl.jpg?sqp=abc&rs=def", artwork);
    }

    [Fact]
    public async Task Artwork_ForUulfFeed_ScrapesTheChannelPage()
    {
        Uri? requested = null;
        var resolver = ScrapingResolver(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><meta property="og:image" content="https://i.ytimg.com/avatar.jpg"></html>"""),
            };
        });

        var resolution = YouTubeChannelResolution.FromRawFeedUrl(
            "https://www.youtube.com/feeds/videos.xml?playlist_id=UULFabcdefghijklmnopqrstuv");
        var artwork = await resolver.TryGetArtworkUrlAsync(resolution, CancellationToken.None);

        Assert.NotNull(artwork);
        // UULF -> UC reverse swap recovers the channel ID, so the avatar comes from the channel page.
        Assert.Contains($"/channel/{ChannelId}", requested!.AbsolutePath);
    }

    [Fact]
    public async Task Artwork_ForUserPlaylist_ScrapesThePlaylistPage()
    {
        Uri? requested = null;
        var resolver = ScrapingResolver(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><meta property="og:image" content="https://i.ytimg.com/pl-thumb.jpg"></html>"""),
            };
        });

        var resolution = YouTubeChannelResolution.FromPlaylistId("PLabc123");
        var artwork = await resolver.TryGetArtworkUrlAsync(resolution, CancellationToken.None);

        Assert.NotNull(artwork);
        Assert.Contains("/playlist", requested!.AbsolutePath);
        Assert.Contains("list=PLabc123", requested.Query);
    }
}
