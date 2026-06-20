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

        // Raw feed URL fallback: already a videos.xml URL, use as-is.
        if (uri.AbsolutePath.Contains("/feeds/videos.xml", StringComparison.OrdinalIgnoreCase))
        {
            return YouTubeChannelResolution.FromRawFeedUrl(uri.ToString());
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
    /// thumbnails), so the channel avatar is scraped separately from the channel
    /// page's og:image meta tag. Best-effort: returns null on any failure.
    /// </summary>
    public async Task<string?> TryGetChannelAvatarUrlAsync(YouTubeChannelResolution resolution, CancellationToken ct)
    {
        var channelId = resolution.ChannelId ?? ExtractChannelIdFromFeedUrl(resolution.RawFeedUrl);
        if (channelId is null)
        {
            return null;
        }

        string html;
        try
        {
            html = await httpClient.GetStringAsync($"https://www.youtube.com/channel/{channelId}", ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var match = OgImagePattern().Match(html);
        return match.Success ? match.Groups["url"].Value : null;
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
