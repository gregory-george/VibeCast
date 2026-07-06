using System.Net;
using System.Text;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;
using Xunit;

namespace VibeCast.Tests;

// Covers the download streaming path reworked in the timeout/truncation fixes: a complete
// body is promoted to a final file, but a body shorter than its declared Content-Length is
// failed with the .partial kept for resume rather than being marked downloaded while corrupt.
public class EpisodeDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_HappyPath_WritesFinalFileAndMarksDownloaded()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var (episodeId, slug, fileName) = await SeedRssEpisodeAsync(factory);
        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, slug);

        try
        {
            var body = Encoding.UTF8.GetBytes("hello podcast body");
            using var httpClient = new HttpClient(
                new StubHttpMessageHandler(_ => BuildResponse(body, declaredLength: body.Length)));
            var downloader = new EpisodeDownloader(httpClient, factory, tracker);

            await downloader.DownloadAsync(episodeId, CancellationToken.None);

            await using var db = factory.CreateDbContext();
            var episode = await db.Episodes.FindAsync(episodeId);
            Assert.NotNull(episode);
            Assert.True(episode!.IsDownloaded);
            Assert.Equal(fileName, episode.DownloadedFileName);

            var finalPath = Path.Combine(feedDir, fileName);
            Assert.True(File.Exists(finalPath));
            Assert.Equal(body, await File.ReadAllBytesAsync(finalPath));
            Assert.False(File.Exists(finalPath + ".partial"));
            Assert.Equal(DownloadStatus.Completed, tracker.Get(episodeId)!.Status);
        }
        finally
        {
            SafeDeleteDir(feedDir);
        }
    }

    [Fact]
    public async Task DownloadAsync_TruncatedBody_FailsAndKeepsPartialForResume()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var (episodeId, slug, fileName) = await SeedRssEpisodeAsync(factory);
        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, slug);

        try
        {
            var body = Encoding.UTF8.GetBytes("only-half");
            // Declare more than we deliver: the read loop ends early on EOF and the
            // truncation guard must fail rather than promote a short file to "downloaded".
            using var httpClient = new HttpClient(
                new StubHttpMessageHandler(_ => BuildResponse(body, declaredLength: body.Length + 100)));
            var downloader = new EpisodeDownloader(httpClient, factory, tracker);

            await downloader.DownloadAsync(episodeId, CancellationToken.None);

            await using var db = factory.CreateDbContext();
            var episode = await db.Episodes.FindAsync(episodeId);
            Assert.NotNull(episode);
            Assert.False(episode!.IsDownloaded);
            Assert.Null(episode.DownloadedFileName);

            var finalPath = Path.Combine(feedDir, fileName);
            Assert.False(File.Exists(finalPath));

            // The partial survives with the bytes received so far, so a later attempt resumes.
            var partialPath = finalPath + ".partial";
            Assert.True(File.Exists(partialPath));
            Assert.Equal(body, await File.ReadAllBytesAsync(partialPath));
            Assert.Equal(DownloadStatus.Failed, tracker.Get(episodeId)!.Status);
        }
        finally
        {
            SafeDeleteDir(feedDir);
        }
    }

    [Fact]
    public async Task DownloadAsync_YouTubeEpisode_NoEnclosure_IsNoOp()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();

        var slug = "yt-" + Guid.NewGuid().ToString("N");
        int episodeId;
        await using (var db = factory.CreateDbContext())
        {
            var feed = new Feed { OriginalUrl = "u", FeedUrl = "u", Slug = slug, Type = FeedType.YouTube, DateAddedUtc = DateTime.UtcNow };
            var episode = new Episode { DedupKey = "yt:v", Title = "V", YouTubeVideoId = "v", Feed = feed };
            db.Episodes.Add(episode);
            await db.SaveChangesAsync();
            episodeId = episode.Id;
        }

        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("network must not be touched")));
        var downloader = new EpisodeDownloader(httpClient, factory, tracker);

        await downloader.DownloadAsync(episodeId, CancellationToken.None);

        Assert.Null(tracker.Get(episodeId));
    }

    private static async Task<(int episodeId, string slug, string fileName)> SeedRssEpisodeAsync(TestDbContextFactory factory)
    {
        var slug = "test-" + Guid.NewGuid().ToString("N");
        await using var db = factory.CreateDbContext();

        var episode = new Episode
        {
            DedupKey = "guid:1",
            Title = "Episode One",
            PublishedAtUtc = new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc),
            EnclosureUrl = "https://cdn.example/ep.mp3",
            EnclosureMediaType = "audio/mpeg",
            Feed = new Feed
            {
                OriginalUrl = "https://example/feed",
                FeedUrl = "https://example/feed",
                Slug = slug,
                Title = "Test Feed",
                Type = FeedType.Rss,
                DateAddedUtc = DateTime.UtcNow,
            },
        };

        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        return (episode.Id, slug, DownloadFileNaming.BuildFileName(episode));
    }

    private static HttpResponseMessage BuildResponse(byte[] body, long declaredLength)
    {
        var content = new StreamContent(new NonSeekableStream(body));
        content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static void SafeDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of test artifacts under the bin/downloads folder.
        }
    }
}
