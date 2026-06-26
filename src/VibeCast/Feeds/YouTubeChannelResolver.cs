using System.Text.RegularExpressions;

namespace VibeCast.Feeds;

/// <summary>
/// Resolves any of @handle / /channel/UC.../ /c/custom / /user/name / a raw UC...
/// channel ID / an already-resolved videos.xml feed URL down to a fetchable feed
/// URL. No API key: the UC... ID is scraped from the channel page's
/// &lt;meta itemprop="channelId"&gt; tag or its canonical /channel/&lt;id&gt; link.
/// </summary>
internal sealed partial class YouTubeChannelResolver(HttpClient httpClient)
{
    public async Task<YouTubeChannelResolution?> TryResolveAsync(string input, CancellationToken ct)
    {
        input = input.Trim();

        if (RawChannelIdPattern().IsMatch(input))
        {
            return YouTubeChannelResolution.FromChannelId(input);
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("youtube.com", StringComparison.Ordinal) && !host.Contains("youtu.be", StringComparison.Ordinal))
        {
            return null;
        }

        // Raw feed URL fallback: already a videos.xml URL — check if it's a custom
        // playlist (playlist_id=PL...) so callers know not to apply ExcludeShorts.
        if (uri.AbsolutePath.Contains("/feeds/videos.xml", StringComparison.OrdinalIgnoreCase))
        {
            var feedQuery = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var rawPlaylistId = feedQuery["playlist_id"];
            if (rawPlaylistId is not null && !rawPlaylistId.StartsWith("UULF", StringComparison.Ordinal))
            {
                return YouTubeChannelResolution.FromPlaylistId(rawPlaylistId);
            }

            return YouTubeChannelResolution.FromRawFeedUrl(uri.ToString());
        }

        // youtube.com/playlist?list=PL... — the human-facing playlist URL.
        if (uri.AbsolutePath.Equals("/playlist", StringComparison.OrdinalIgnoreCase))
        {
            var playlistQuery = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var listId = playlistQuery["list"];
            if (listId is not null)
            {
                return YouTubeChannelResolution.FromPlaylistId(listId);
            }
        }

        // /channel/UC... carries the ID directly; no scrape needed.
        var channelMatch = ChannelPathPattern().Match(uri.AbsolutePath);
        if (channelMatch.Success)
        {
            return YouTubeChannelResolution.FromChannelId(channelMatch.Groups["id"].Value);
        }

        // /@handle, /c/custom, /user/name all require scraping the channel page,
        // since the feed endpoint only accepts the UC... channel ID.
        var scrapedId = await ScrapeChannelIdAsync(uri, ct);
        return scrapedId is null ? null : YouTubeChannelResolution.FromChannelId(scrapedId);
    }

    private async Task<string?> ScrapeChannelIdAsync(Uri channelPageUrl, CancellationToken ct)
    {
        string html;
        try
        {
            html = await httpClient.GetStringAsync(channelPageUrl, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var metaMatch = MetaChannelIdPattern().Match(html);
        if (metaMatch.Success)
        {
            return metaMatch.Groups["id"].Value;
        }

        var canonicalMatch = CanonicalChannelIdPattern().Match(html);
        return canonicalMatch.Success ? canonicalMatch.Groups["id"].Value : null;
    }

    /// <summary>
    /// YouTube's videos.xml carries no channel-level artwork (only per-video
    /// thumbnails), so the feed's cover is scraped from a YouTube web page's
    /// og:image meta tag. For channel feeds that's the channel page (its avatar);
    /// for user playlists (playlist_id=PL...), which carry no channel ID, it's the
    /// playlist page (its own thumbnail). Best-effort: returns null on any failure.
    /// </summary>
    public async Task<string?> TryGetArtworkUrlAsync(YouTubeChannelResolution resolution, CancellationToken ct)
    {
        var channelId = resolution.ChannelId ?? ExtractChannelIdFromFeedUrl(resolution.RawFeedUrl);
        if (channelId is not null)
        {
            return await TryScrapeOgImageAsync($"https://www.youtube.com/channel/{channelId}", ct);
        }

        // No channel ID derivable (an ordinary PL... playlist doesn't embed one).
        // Fall back to the playlist page's own og:image thumbnail.
        var playlistId = ExtractPlaylistIdFromFeedUrl(resolution.RawFeedUrl);
        if (playlistId is not null)
        {
            return await TryScrapeOgImageAsync($"https://www.youtube.com/playlist?list={playlistId}", ct);
        }

        return null;
    }

    private async Task<string?> TryScrapeOgImageAsync(string pageUrl, CancellationToken ct)
    {
        string html;
        try
        {
            html = await httpClient.GetStringAsync(pageUrl, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var match = OgImagePattern().Match(html);
        // The meta tag's content is HTML-encoded; playlist thumbnails carry a
        // signed query string (?sqp=...&rs=...) whose '&' arrive as '&amp;' and
        // must be decoded or the artwork fetch requests a malformed URL.
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value) : null;
    }

    private static string? ExtractChannelIdFromFeedUrl(string? feedUrl)
    {
        if (feedUrl is null || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var channelId = query["channel_id"];
        if (channelId is not null)
        {
            return channelId;
        }

        var playlistId = query["playlist_id"];
        if (playlistId is not null && playlistId.StartsWith("UULF", StringComparison.Ordinal))
        {
            // UULF -> UC prefix swap reverses the long-form-only playlist ID back to the channel ID.
            return "UC" + playlistId[4..];
        }

        return null;
    }

    /// <summary>
    /// Extracts a user-playlist ID (playlist_id=PL...) from a videos.xml feed URL.
    /// Excludes the UULF... long-form variant, which maps back to a channel ID and
    /// is handled by <see cref="ExtractChannelIdFromFeedUrl"/> instead.
    /// </summary>
    private static string? ExtractPlaylistIdFromFeedUrl(string? feedUrl)
    {
        if (feedUrl is null || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var playlistId = System.Web.HttpUtility.ParseQueryString(uri.Query)["playlist_id"];
        return playlistId is not null && !playlistId.StartsWith("UULF", StringComparison.Ordinal)
            ? playlistId
            : null;
    }

    [GeneratedRegex("^UC[A-Za-z0-9_-]{20,}$")]
    private static partial Regex RawChannelIdPattern();

    [GeneratedRegex("/channel/(?<id>UC[A-Za-z0-9_-]{10,})")]
    private static partial Regex ChannelPathPattern();

    [GeneratedRegex("<meta\\s+itemprop=\"channelId\"\\s+content=\"(?<id>UC[A-Za-z0-9_-]{10,})\"")]
    private static partial Regex MetaChannelIdPattern();

    [GeneratedRegex("<link\\s+rel=\"canonical\"\\s+href=\"https://www\\.youtube\\.com/channel/(?<id>UC[A-Za-z0-9_-]{10,})\"")]
    private static partial Regex CanonicalChannelIdPattern();

    [GeneratedRegex("<meta\\s+property=\"og:image\"\\s+content=\"(?<url>[^\"]+)\"")]
    private static partial Regex OgImagePattern();
}
