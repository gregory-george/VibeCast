namespace VibeCast.Feeds;

internal sealed record ParsedFeed(string? Title, string? ArtworkUrl, IReadOnlyList<ParsedEpisode> Episodes);

internal sealed record ParsedEpisode(
    string DedupKey,
    string Title,
    DateTimeOffset PublishedAtUtc,
    // False when the feed gave no usable date and PublishedAtUtc is the "today" fallback --
    // refresh must not re-stamp a stored date with that ever-moving value.
    bool HasPublishedDate,
    string? DescriptionHtml,
    string? ArtworkUrl,
    int? DurationSeconds,
    string? EnclosureUrl,
    string? EnclosureMediaType,
    string? YouTubeVideoId);
