using System.Security.Cryptography;
using System.Text;

namespace VibeCast.Feeds;

/// <summary>
/// Computes the composite de-dup identity for an episode. Prefixed by source
/// ("guid:"/"url:"/"hash:"/"yt:") so the resolution path used is visible later if
/// duplicate-tracking needs debugging (see the accepted-cost note in the build plan:
/// a feed that changes a GUID can produce a non-aging duplicate).
/// </summary>
internal static class DedupKeyComputer
{
    public static string ForRss(string? guid, string? enclosureUrl, string title, DateTimeOffset publishedAtUtc)
    {
        if (!string.IsNullOrWhiteSpace(guid))
        {
            return $"guid:{guid.Trim()}";
        }

        var normalizedUrl = NormalizeEnclosureUrl(enclosureUrl);
        if (normalizedUrl is not null)
        {
            return $"url:{normalizedUrl}";
        }

        return $"hash:{Hash($"{title}|{publishedAtUtc:O}")}";
    }

    public static string ForYouTube(string videoId) => $"yt:{videoId.Trim()}";

    private static string? NormalizeEnclosureUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Strip the query string / tracking params so the same episode doesn't
        // look new every refresh.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
