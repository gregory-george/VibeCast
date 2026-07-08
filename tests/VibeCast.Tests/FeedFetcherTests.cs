using System.Net;
using System.Text;
using System.Xml;
using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

// FeedFetcher is the boundary where hostile feed bytes enter the app. These pin the
// untrusted-input hard rules (CLAUDE.md): the 20 MB cap applies whether or not the
// server declares a Content-Length, DTDs stay prohibited (no XXE / billion-laughs),
// and the XML declaration's own encoding is honored instead of a forced decode.
public class FeedFetcherTests
{
    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Fetcher Test Feed</title>
            <item>
              <guid>ep-1</guid>
              <title>Episode 1</title>
              <pubDate>Mon, 02 Jan 2006 15:04:05 GMT</pubDate>
              <enclosure url="https://cdn.example/ep1.mp3" type="audio/mpeg" length="1" />
            </item>
          </channel>
        </rss>
        """;

    private static FeedFetcher BuildFetcher(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHttpMessageHandler(responder)));

    [Fact]
    public async Task ValidFeed_IsFetchedAndParsed()
    {
        var fetcher = BuildFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleRss),
        });

        var parsed = await fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None);

        Assert.Equal("Fetcher Test Feed", parsed.Title);
        Assert.Single(parsed.Episodes);
    }

    [Fact]
    public async Task DeclaredOversizedBody_IsRejectedUpFront()
    {
        // The body is only 4 bytes, so the streaming cap can never trip -- if this
        // throws, it's the declared Content-Length check rejecting before the read.
        var fetcher = BuildFetcher(_ =>
        {
            var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("tiny")));
            content.Headers.ContentLength = 21L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None));

        Assert.Contains("20 MB", ex.Message);
    }

    [Fact]
    public async Task UndeclaredOversizedBody_IsRejectedByStreamingCap()
    {
        // No Content-Length (non-seekable stream), so only the streaming cap can stop it.
        var oversized = new byte[21 * 1024 * 1024];
        var fetcher = BuildFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(oversized)),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None));

        Assert.Contains("20 MB", ex.Message);
    }

    [Fact]
    public async Task ExternalEntity_IsNotResolved_NoXxe()
    {
        // XXE guard: an external entity must never be expanded (no file/SSRF read).
        // XDocument.Load runs with a null resolver, so the reference resolves to
        // nothing -- the title comes back empty rather than leaking file contents.
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <rss version="2.0"><channel><title>&xxe;</title></channel></rss>
            """;
        var fetcher = BuildFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xxe),
        });

        var parsed = await fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(parsed.Title));
    }

    [Fact]
    public async Task BillionLaughs_IsRejectedByEntityExpansionCap()
    {
        // Nested internal entities expand exponentially; the reader's
        // MaxCharactersFromEntities cap must stop it before it exhausts memory.
        const string laughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
              <!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;">
              <!ENTITY lol5 "&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;">
              <!ENTITY lol6 "&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;">
              <!ENTITY lol7 "&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;">
              <!ENTITY lol8 "&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;">
              <!ENTITY lol9 "&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;">
            ]>
            <rss version="2.0"><channel><title>&lol9;</title></channel></rss>
            """;
        var fetcher = BuildFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(laughs),
        });

        await Assert.ThrowsAsync<XmlException>(
            () => fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None));
    }

    [Fact]
    public async Task NonUtf8Feed_DeclaredEncoding_IsHonored()
    {
        // Latin-1 bytes: "é" is 0xE9, which is invalid UTF-8. A forced UTF-8 decode
        // would mangle the title; loading from the raw bytes must not.
        const string xml = """
            <?xml version="1.0" encoding="iso-8859-1"?>
            <rss version="2.0"><channel><title>Café Chrono</title></channel></rss>
            """;
        var fetcher = BuildFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.Latin1.GetBytes(xml)),
        });

        var parsed = await fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None);

        Assert.Equal("Café Chrono", parsed.Title);
    }

    [Fact]
    public async Task HttpErrorStatus_Throws()
    {
        var fetcher = BuildFetcher(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchAndParseAsync("https://example/feed.xml", CancellationToken.None));
    }
}
