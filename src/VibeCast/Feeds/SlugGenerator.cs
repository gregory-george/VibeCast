using System.Text;

namespace VibeCast.Feeds;

/// <summary>
/// Builds the filesystem-safe folder name used under downloads/&lt;feed-slug&gt;/.
/// Assigned once when a feed is added and never changed afterward, since downloaded
/// files (Phase 3) live under it.
/// </summary>
internal static class SlugGenerator
{
    private const int MaxLength = 60;

    public static string Generate(string seed, IReadOnlyCollection<string> existingSlugs)
    {
        var baseSlug = Slugify(seed, MaxLength);
        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = "feed";
        }

        var existing = new HashSet<string>(existingSlugs, StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseSlug))
        {
            return baseSlug;
        }

        var attempt = 2;
        string candidate;
        do
        {
            candidate = $"{baseSlug}-{attempt++}";
        } while (existing.Contains(candidate));

        return candidate;
    }

    /// <summary>Lowercase, alphanumeric-and-dash-only slug, e.g. for downloaded filenames too.</summary>
    public static string Slugify(string input, int maxLength)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var builder = new StringBuilder();
        var lastWasDash = false;

        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var result = builder.ToString().TrimEnd('-');
        return result.Length > maxLength ? result[..maxLength].TrimEnd('-') : result;
    }
}
