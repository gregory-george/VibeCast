using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VibeCast.Feeds;

/// <summary>
/// Parses RSS 2.0 and Atom (YouTube's videos.xml is Atom + yt:/media: extensions)
/// into a source-agnostic ParsedFeed/ParsedEpisode shape. Uses XDocument (LINQ-to-XML)
/// for full control over the itunes:/media:/yt: extension namespaces, per CLAUDE.md.
/// </summary>
internal static class FeedDocumentParser
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";

    public static ParsedFeed Parse(XDocument document)
    {
        var root = document.Root ?? throw new XmlException("Empty XML document.");

        return root.Name.LocalName switch
        {
            "rss" => ParseRss(root),
            "feed" => ParseAtom(root),
            _ => throw new XmlException($"Unrecognized feed root element '{root.Name.LocalName}'."),
        };
    }

    private static ParsedFeed ParseRss(XElement rssRoot)
    {
        var channel = rssRoot.Element("channel") ?? throw new XmlException("RSS feed is missing <channel>.");

        var title = channel.Element("title")?.Value.Trim();
        var artwork = channel.Element(Itunes + "image")?.Attribute("href")?.Value
            ?? channel.Element("image")?.Element("url")?.Value
            ?? channel.Element(Media + "thumbnail")?.Attribute("url")?.Value;

        var episodes = channel.Elements("item")
            .Select(ParseRssItem)
            .Select(BuildParsedEpisode)
            .ToList();

        return new ParsedFeed(title, artwork, episodes);
    }

    private static RawEpisodeFields ParseRssItem(XElement item)
    {
        var guid = item.Element("guid")?.Value.Trim();
        var title = item.Element("title")?.Value.Trim() ?? "(untitled)";
        var publishedAtUtc = ParseDate(item.Element("pubDate")?.Value);

        var description = item.Element(Content + "encoded")?.Value
            ?? item.Element("description")?.Value
            ?? item.Element(Itunes + "summary")?.Value;

        var enclosure = item.Element("enclosure");
        var enclosureUrl = enclosure?.Attribute("url")?.Value;
        var enclosureType = enclosure?.Attribute("type")?.Value;

        if (enclosureUrl is null)
        {
            var mediaContent = item.Element(Media + "content");
            enclosureUrl = mediaContent?.Attribute("url")?.Value;
            enclosureType = mediaContent?.Attribute("type")?.Value;
        }

        var artwork = item.Element(Itunes + "image")?.Attribute("href")?.Value
            ?? item.Element(Media + "thumbnail")?.Attribute("url")?.Value;

        var durationSeconds = ParseItunesDuration(item.Element(Itunes + "duration")?.Value);

        return new RawEpisodeFields(guid, title, publishedAtUtc, description, artwork, durationSeconds, enclosureUrl, enclosureType, YouTubeVideoId: null);
    }

    private static ParsedFeed ParseAtom(XElement feedRoot)
    {
        var title = feedRoot.Element(Atom + "title")?.Value.Trim();
        var artwork = feedRoot.Element(Media + "thumbnail")?.Attribute("url")?.Value
            ?? feedRoot.Element(Atom + "logo")?.Value
            ?? feedRoot.Element(Atom + "icon")?.Value;

        var episodes = feedRoot.Elements(Atom + "entry")
            .Select(ParseAtomEntry)
            .Select(BuildParsedEpisode)
            .ToList();

        return new ParsedFeed(title, artwork, episodes);
    }

    private static RawEpisodeFields ParseAtomEntry(XElement entry)
    {
        var id = entry.Element(Atom + "id")?.Value.Trim();
        var title = entry.Element(Atom + "title")?.Value.Trim() ?? "(untitled)";
        var publishedRaw = entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value;
        var publishedAtUtc = ParseDate(publishedRaw);

        // Present on YouTube's videos.xml entries; absent on generic Atom feeds,
        // which then fall back to the RSS-style guid/url/hash chain via <id>.
        var videoId = entry.Element(Yt + "videoId")?.Value.Trim();

        var mediaGroup = entry.Element(Media + "group");
        var description = mediaGroup?.Element(Media + "description")?.Value
            ?? entry.Element(Atom + "summary")?.Value
            ?? entry.Element(Atom + "content")?.Value;

        var artwork = mediaGroup?.Element(Media + "thumbnail")?.Attribute("url")?.Value;

        string? enclosureUrl = null;
        string? enclosureType = null;
        var enclosureLink = entry.Elements(Atom + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") == "enclosure");
        if (enclosureLink is not null)
        {
            enclosureUrl = enclosureLink.Attribute("href")?.Value;
            enclosureType = enclosureLink.Attribute("type")?.Value;
        }

        return new RawEpisodeFields(id, title, publishedAtUtc, description, artwork, DurationSeconds: null, enclosureUrl, enclosureType, videoId);
    }

    private static ParsedEpisode BuildParsedEpisode(RawEpisodeFields raw)
    {
        var dedupKey = string.IsNullOrWhiteSpace(raw.YouTubeVideoId)
            ? DedupKeyComputer.ForRss(raw.Guid, raw.EnclosureUrl, raw.Title, raw.PublishedAtUtc)
            : DedupKeyComputer.ForYouTube(raw.YouTubeVideoId);

        return new ParsedEpisode(
            dedupKey,
            raw.Title,
            raw.PublishedAtUtc,
            raw.DescriptionHtml,
            raw.ArtworkUrl,
            raw.DurationSeconds,
            raw.EnclosureUrl,
            raw.EnclosureMediaType,
            raw.YouTubeVideoId);
    }

    // RFC 822/2822 named timezone abbreviations as used in <pubDate> (e.g. "PDT", "GMT").
    // DateTimeOffset.TryParse only understands numeric offsets and "Z"/"UTC", so these
    // silently fail to parse and would otherwise fall through to the "unknown -> today"
    // case, dating every episode as today regardless of its real pubDate.
    private static readonly Dictionary<string, string> RfcZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+00:00",
        ["GMT"] = "+00:00",
        ["EST"] = "-05:00",
        ["EDT"] = "-04:00",
        ["CST"] = "-06:00",
        ["CDT"] = "-05:00",
        ["MST"] = "-07:00",
        ["MDT"] = "-06:00",
        ["PST"] = "-08:00",
        ["PDT"] = "-07:00",
    };

    private static readonly Regex TrailingRfcZone = new(
        @"\s([A-Za-z]{2,3})$", RegexOptions.Compiled);

    private static DateTimeOffset ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DateTimeOffset.UtcNow;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        var zoneMatch = TrailingRfcZone.Match(raw.Trim());
        if (zoneMatch.Success && RfcZoneOffsets.TryGetValue(zoneMatch.Groups[1].Value, out var offset))
        {
            var withOffset = string.Concat(raw.Trim().AsSpan(0, zoneMatch.Index), " ", offset);
            if (DateTimeOffset.TryParse(withOffset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedWithOffset))
            {
                return parsedWithOffset;
            }
        }

        // Unknown/unparsable date: assume today. Consistent with the 90-day
        // auto-download cutoff's "unknown date -> today" rule (CLAUDE.md).
        return DateTimeOffset.UtcNow;
    }

    private static int? ParseItunesDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();
        if (!raw.Contains(':'))
        {
            return int.TryParse(raw, out var plainSeconds) ? plainSeconds : null;
        }

        var seconds = 0;
        foreach (var part in raw.Split(':'))
        {
            if (!int.TryParse(part, out var value))
            {
                return null;
            }

            seconds = (seconds * 60) + value;
        }

        return seconds;
    }

    private sealed record RawEpisodeFields(
        string? Guid,
        string Title,
        DateTimeOffset PublishedAtUtc,
        string? DescriptionHtml,
        string? ArtworkUrl,
        int? DurationSeconds,
        string? EnclosureUrl,
        string? EnclosureMediaType,
        string? YouTubeVideoId);
}
