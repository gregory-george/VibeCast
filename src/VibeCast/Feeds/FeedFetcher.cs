using System.Xml.Linq;

namespace VibeCast.Feeds;

internal sealed class FeedFetcher(HttpClient httpClient)
{
    public async Task<ParsedFeed> FetchAndParseAsync(string feedUrl, CancellationToken ct)
    {
        var xml = await httpClient.GetStringAsync(feedUrl, ct);
        var document = XDocument.Parse(xml);
        return FeedDocumentParser.Parse(document);
    }
}
