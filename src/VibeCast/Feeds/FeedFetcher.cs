using System.Xml.Linq;

namespace VibeCast.Feeds;

/// <summary>
/// Fetches and parses a feed. Feeds are untrusted input (CLAUDE.md), so the response
/// body is read through a hard size cap instead of being buffered unbounded into a
/// string -- a hostile or broken feed can't exhaust memory. Parsing runs off the raw
/// bytes so the XML declaration's own encoding is honored.
/// </summary>
internal sealed class FeedFetcher(HttpClient httpClient)
{
    private const int BufferSize = 65536;

    // Generous ceiling for an XML feed body; real feeds are far smaller. Anything past
    // this is treated as hostile/broken and rejected before it reaches the parser.
    private const long MaxFeedBytes = 20 * 1024 * 1024;

    public async Task<ParsedFeed> FetchAndParseAsync(string feedUrl, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Reject up front when the server advertises an oversized body.
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxFeedBytes)
        {
            throw new HttpRequestException($"Feed body exceeds the {MaxFeedBytes / (1024 * 1024)} MB limit.");
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await CopyCappedAsync(contentStream, buffer, ct);
        buffer.Position = 0;

        // Load from the stream rather than XDocument.Parse(string): XDocument.Load reads
        // the encoding from the XML declaration / BOM, so a non-UTF-8 feed isn't mangled
        // by a forced decode. DTD processing stays prohibited (the default), so this is
        // not an XXE / billion-laughs vector.
        var document = XDocument.Load(buffer);
        return FeedDocumentParser.Parse(document);
    }

    private static async Task CopyCappedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxFeedBytes)
            {
                throw new HttpRequestException($"Feed body exceeds the {MaxFeedBytes / (1024 * 1024)} MB limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
