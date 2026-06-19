using VibeCast.Data;
using VibeCast.Feeds;

namespace VibeCast.Downloads;

internal static class DownloadFileNaming
{
    private const int MaxTitleSlugLength = 80;

    /// <summary>
    /// Builds the saved filename for an RSS episode: a human-readable, sanitized
    /// title slug for readability, suffixed with the episode's own DB id to
    /// guarantee uniqueness deterministically (no collision-retry needed). The
    /// extension always comes from the enclosure's media type (see MediaTypeExtensions).
    /// </summary>
    public static string BuildFileName(Episode episode)
    {
        var titleSlug = SlugGenerator.Slugify(episode.Title, MaxTitleSlugLength);
        if (string.IsNullOrEmpty(titleSlug))
        {
            titleSlug = "episode";
        }

        var extension = MediaTypeExtensions.ToExtension(episode.EnclosureMediaType);
        return $"{episode.PublishedAtUtc:yyyy-MM-dd}-{titleSlug}-{episode.Id}{extension}";
    }
}
