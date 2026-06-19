using VibeCast.Data;

namespace VibeCast.Feeds;

internal static class EpisodeMapper
{
    public static Episode ToEntity(ParsedEpisode parsed) => new()
    {
        DedupKey = parsed.DedupKey,
        Title = parsed.Title,
        PublishedAtUtc = parsed.PublishedAtUtc.UtcDateTime,
        DescriptionHtml = parsed.DescriptionHtml,
        ArtworkUrl = parsed.ArtworkUrl,
        DurationSeconds = parsed.DurationSeconds,
        EnclosureUrl = parsed.EnclosureUrl,
        EnclosureMediaType = parsed.EnclosureMediaType,
        YouTubeVideoId = parsed.YouTubeVideoId,
    };
}
