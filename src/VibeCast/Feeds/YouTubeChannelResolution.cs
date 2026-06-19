namespace VibeCast.Feeds;

/// <summary>Either a resolved UC... channel ID or a raw feed URL pasted/discovered directly.</summary>
internal sealed record YouTubeChannelResolution(string? ChannelId, string? RawFeedUrl)
{
    public static YouTubeChannelResolution FromChannelId(string channelId) => new(channelId, null);

    public static YouTubeChannelResolution FromRawFeedUrl(string feedUrl) => new(null, feedUrl);

    public string ToFeedUrl(bool excludeShorts)
    {
        if (RawFeedUrl is not null)
        {
            return RawFeedUrl;
        }

        var id = ChannelId!;
        if (excludeShorts && id.StartsWith("UC", StringComparison.Ordinal))
        {
            // UC -> UULF prefix swap selects the long-form-only playlist feed.
            var playlistId = "UULF" + id[2..];
            return $"https://www.youtube.com/feeds/videos.xml?playlist_id={playlistId}";
        }

        return $"https://www.youtube.com/feeds/videos.xml?channel_id={id}";
    }
}
