namespace VibeCast.Feeds;

internal sealed record ParsedFeed(string? Title, string? ArtworkUrl, IReadOnlyList<ParsedEpisode> Episodes);

internal sealed record ParsedEpisode(
    string DedupKey,
    string Title,
    DateTimeOffset PublishedAtUtc,
    string? DescriptionHtml,
    string? ArtworkUrl,
    int? DurationSeconds,
    string? EnclosureUrl,
    string? EnclosureMediaType,
    string? YouTubeVideoId);
