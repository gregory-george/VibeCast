namespace VibeCast.Data;

internal sealed class Episode
{
    public int Id { get; set; }

    public int FeedId { get; set; }

    public Feed Feed { get; set; } = null!;

    /// <summary>
    /// Composite de-dup identity, scoped per feed (see the unique index in
    /// AppDbContext). RSS resolution order: guid -> normalized enclosure URL ->
    /// hash(title+pubdate). YouTube: the watch?v= video ID. Computed once at
    /// ingest by DedupKeyComputer; the rest of the app trusts this column.
    /// </summary>
    public required string DedupKey { get; set; }

    public required string Title { get; set; }

    public DateTime PublishedAtUtc { get; set; }

    /// <summary>Raw, untrusted HTML from the feed. Sanitize at render time, never store sanitized.</summary>
    public string? DescriptionHtml { get; set; }

    public string? ArtworkUrl { get; set; }

    public int? DurationSeconds { get; set; }

    /// <summary>
    /// YouTube only. Set when the video is a scheduled premiere/live stream that hasn't
    /// aired yet (its watch page reports lengthSeconds=0 and a scheduledStartTime). Null
    /// for RSS, for aired videos, and once a re-scrape after airing lands a real duration.
    /// Drives the "Upcoming" indicator; a non-null value with a null DurationSeconds means
    /// the item is still awaiting air.
    /// </summary>
    public DateTime? ScheduledStartUtc { get; set; }

    /// <summary>RSS only.</summary>
    public string? EnclosureUrl { get; set; }

    /// <summary>RSS only. Drives the saved filename/extension (never the URL or feed-supplied name).</summary>
    public string? EnclosureMediaType { get; set; }

    /// <summary>YouTube only. The watch?v= link is https://www.youtube.com/watch?v={YouTubeVideoId}.</summary>
    public string? YouTubeVideoId { get; set; }

    // State flags (CLAUDE.md "Data model invariants"). Played and archived currently
    // move together but are tracked as distinct flags; mark-as-played, the archive UI,
    // downloads, and playback all read them.
    public bool IsPlayed { get; set; }

    public bool IsArchived { get; set; }

    public bool IsDownloaded { get; set; }

    /// <summary>
    /// The file name actually saved under downloads/&lt;feed-slug&gt;/, derived from
    /// the enclosure's media type at download time -- never from the URL or any
    /// feed-supplied name. Null until downloaded.
    /// </summary>
    public string? DownloadedFileName { get; set; }

    public int PlaybackPositionSeconds { get; set; }
}
