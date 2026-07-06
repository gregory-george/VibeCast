using System.Xml;
using System.Xml.Linq;
using VibeCast.Feeds;
using Xunit;

namespace VibeCast.Tests;

public class FeedDocumentParserTests
{
    private static ParsedFeed ParseXml(string xml) => FeedDocumentParser.Parse(XDocument.Parse(xml));

    private const string RssTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"
             xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"
             xmlns:content="http://purl.org/rss/1.0/modules/content/"
             xmlns:media="http://search.yahoo.com/mrss/">
          <channel>
            <title>Test Podcast</title>
            <itunes:image href="https://example/cover.jpg" />
            <item>
              <guid>episode-guid-1</guid>
              <title>First Episode</title>
              <pubDate>Mon, 02 Jan 2006 15:04:05 PST</pubDate>
              <itunes:duration>1:02:03</itunes:duration>
              <content:encoded><![CDATA[<p>Show notes here</p>]]></content:encoded>
              <enclosure url="https://cdn.example/ep1.mp3" type="audio/mpeg" length="123" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void ParseRss_ReadsChannelTitleAndArtwork()
    {
        var feed = ParseXml(RssTemplate);
        Assert.Equal("Test Podcast", feed.Title);
        Assert.Equal("https://example/cover.jpg", feed.ArtworkUrl);
    }

    [Fact]
    public void ParseRss_ReadsEpisodeFields()
    {
        var ep = Assert.Single(ParseXml(RssTemplate).Episodes);
        Assert.Equal("guid:episode-guid-1", ep.DedupKey);
        Assert.Equal("First Episode", ep.Title);
        Assert.Equal("https://cdn.example/ep1.mp3", ep.EnclosureUrl);
        Assert.Equal("audio/mpeg", ep.EnclosureMediaType);
        Assert.Equal(3723, ep.DurationSeconds);
        Assert.Contains("Show notes here", ep.DescriptionHtml);
        Assert.Null(ep.YouTubeVideoId);
    }

    [Fact]
    public void ParseRss_NamedTimezone_IsHonored_NotTreatedAsToday()
    {
        // "PST" isn't understood by DateTimeOffset.TryParse; the parser's RFC-822 zone
        // fallback must map it to -08:00 rather than silently dating the item "today".
        var ep = Assert.Single(ParseXml(RssTemplate).Episodes);
        Assert.Equal(
            new DateTimeOffset(2006, 1, 2, 23, 4, 5, TimeSpan.Zero),
            ep.PublishedAtUtc.ToUniversalTime());
    }

    [Theory]
    [InlineData("3723", 3723)]      // plain seconds
    [InlineData("05:10", 310)]      // mm:ss
    [InlineData("1:02:03", 3723)]   // h:mm:ss
    [InlineData("bogus", null)]     // unparsable -> null
    public void ParseRss_ItunesDuration_HandlesFormats(string duration, int? expected)
    {
        var xml = RssTemplate.Replace("<itunes:duration>1:02:03</itunes:duration>", $"<itunes:duration>{duration}</itunes:duration>");
        var ep = Assert.Single(ParseXml(xml).Episodes);
        Assert.Equal(expected, ep.DurationSeconds);
    }

    [Fact]
    public void ParseRss_FallsBackToMediaContent_WhenNoEnclosure()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/">
              <channel>
                <title>MC</title>
                <item>
                  <guid>g2</guid>
                  <title>Media Content Ep</title>
                  <pubDate>Tue, 03 Jan 2006 00:00:00 GMT</pubDate>
                  <media:content url="https://cdn.example/ep2.m4a" type="audio/mp4" />
                </item>
              </channel>
            </rss>
            """;
        var ep = Assert.Single(ParseXml(xml).Episodes);
        Assert.Equal("https://cdn.example/ep2.m4a", ep.EnclosureUrl);
        Assert.Equal("audio/mp4", ep.EnclosureMediaType);
    }

    [Fact]
    public void ParseAtom_YouTube_UsesVideoIdForDedupAndReadsMediaGroup()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom"
                  xmlns:yt="http://www.youtube.com/xml/schemas/2015"
                  xmlns:media="http://search.yahoo.com/mrss/">
              <title>Test Channel</title>
              <entry>
                <id>yt:video:VID123</id>
                <yt:videoId>VID123</yt:videoId>
                <title>A Video</title>
                <published>2026-01-02T03:04:05+00:00</published>
                <media:group>
                  <media:description>Video description</media:description>
                  <media:thumbnail url="https://i.ytimg.com/vi/VID123/hq.jpg" />
                </media:group>
              </entry>
            </feed>
            """;
        var feed = ParseXml(xml);
        Assert.Equal("Test Channel", feed.Title);
        var ep = Assert.Single(feed.Episodes);
        Assert.Equal("yt:VID123", ep.DedupKey);
        Assert.Equal("VID123", ep.YouTubeVideoId);
        Assert.Equal("Video description", ep.DescriptionHtml);
        Assert.Equal("https://i.ytimg.com/vi/VID123/hq.jpg", ep.ArtworkUrl);
        Assert.Null(ep.EnclosureUrl);
        Assert.Null(ep.DurationSeconds);
    }

    [Fact]
    public void Parse_UnrecognizedRoot_Throws()
    {
        Assert.Throws<XmlException>(() => ParseXml("<html><body/></html>"));
    }

    [Fact]
    public void Parse_RssWithoutChannel_Throws()
    {
        Assert.Throws<XmlException>(() => ParseXml("<rss version=\"2.0\"></rss>"));
    }
}
