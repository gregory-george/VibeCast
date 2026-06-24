namespace VibeCast.Feeds;

/// <summary>
/// Either a resolved UC... channel ID, a raw feed URL, or a custom playlist (PL...).
/// <see cref="IsCustomPlaylist"/> is true for user-created playlists — ExcludeShorts
/// does not apply to them.
/// </summary>
internal sealed record YouTubeChannelResolution(string? ChannelId, string? RawFeedUrl, bool IsCustomPlaylist = false)
{
    public static YouTubeChannelResolution FromChannelId(string channelId) => new(channelId, null);

    public static YouTubeChannelResolution FromRawFeedUrl(string feedUrl) => new(null, feedUrl);

    /// <summary>
    /// Creates a resolution for a user-created playlist (playlist_id=PL...).
    /// ExcludeShorts is not applicable; the playlist is already a specific set of videos.
    /// </summary>
    public static YouTubeChannelResolution FromPlaylistId(string playlistId) =>
        new(null, $"https://www.youtube.com/feeds/videos.xml?playlist_id={playlistId}", IsCustomPlaylist: true);

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
