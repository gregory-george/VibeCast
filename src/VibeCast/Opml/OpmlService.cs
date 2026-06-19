using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using VibeCast.Data;

namespace VibeCast.Opml;

/// <summary>
/// OPML import/export (CLAUDE.md §8): subscription migration only -- round-trips
/// just the list of feed URLs (RSS and YouTube alike, since YouTube channels are
/// stored as feed URLs too). Does not carry per-feed settings, played/archive
/// state, or episode history; "backup" is just copying the folder.
/// </summary>
internal sealed class OpmlService(IDbContextFactory<AppDbContext> dbContextFactory)
{
    public async Task<byte[]> ExportAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var feeds = await db.Feeds
            .OrderBy(f => f.Title)
            .Select(f => new { Title = f.Title ?? f.OriginalUrl, f.FeedUrl })
            .ToListAsync(ct);

        var body = new XElement("body",
            feeds.Select(f => new XElement("outline",
                new XAttribute("text", f.Title),
                new XAttribute("title", f.Title),
                new XAttribute("type", "rss"),
                new XAttribute("xmlUrl", f.FeedUrl))));

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("opml", new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", "VibeCast subscriptions")),
                body));

        using var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            await doc.SaveAsync(writer, SaveOptions.None, ct);
        }

        return stream.ToArray();
    }

    /// <summary>Extracts feed/channel URLs from an OPML document's xmlUrl attributes.</summary>
    public static IReadOnlyList<string> ParseFeedUrls(string opmlXml)
    {
        var doc = XDocument.Parse(opmlXml);
        return doc.Descendants("outline")
            .Select(o => o.Attribute("xmlUrl")?.Value)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Distinct()
            .ToList();
    }
}
