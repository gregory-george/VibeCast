namespace VibeCast.Data;

internal sealed class Feed
{
    public int Id { get; set; }

    /// <summary>What the user pasted when adding the feed.</summary>
    public required string OriginalUrl { get; set; }

    /// <summary>
    /// The URL actually fetched on refresh. For YouTube this is the resolved
    /// videos.xml URL (channel_id= or, with ExcludeShorts, playlist_id=UULF...).
    /// </summary>
    public required string FeedUrl { get; set; }

    public FeedType Type { get; set; }

    public string? Title { get; set; }

    /// <summary>Filesystem-safe folder name under downloads/, unique, assigned once and stable.</summary>
    public required string Slug { get; set; }

    public string? ArtworkUrl { get; set; }

    /// <summary>YouTube only: swaps the feed URL to the UULF long-form playlist (excludes Shorts).</summary>
    public bool ExcludeShorts { get; set; }

    /// <summary>Per-feed override of the global auto-download-all-new default.</summary>
    public bool AutoDownloadEnabled { get; set; } = true;

    /// <summary>
    /// Per-feed override of the 90-day auto-download cutoff. Null means "download
    /// all" (no age limit). Evaluated at ingest against PublishedAtUtc.
    /// </summary>
    public int? AutoDownloadMaxAgeDays { get; set; } = 90;

    public DateTime DateAddedUtc { get; set; }

    public DateTime? LastRefreshedUtc { get; set; }

    public List<Episode> Episodes { get; set; } = [];
}
