using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Episodes;
using Xunit;

namespace VibeCast.Tests;

// Mark-as-played / Unarchive invariants (CLAUDE.md): played RSS files are deleted
// immediately (deferred only when locked -- flags still move), and unarchive
// re-downloads only when the file is actually gone. YouTube is flags-only.
public class EpisodeStateServiceTests
{
    [Fact]
    public async Task MarkAsPlayed_Rss_DeletesFileAndArchives()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var service = BuildService(factory, tracker);
        var (episodeId, slug, filePath) = await SeedDownloadedEpisodeAsync(factory);

        try
        {
            tracker.Set(new DownloadProgressSnapshot(episodeId, "Ep", "Feed", DownloadStatus.Completed, 1, 1, null));

            await service.MarkAsPlayedAsync(episodeId, CancellationToken.None);

            var episode = await LoadEpisodeAsync(factory, episodeId);
            Assert.True(episode.IsPlayed);
            Assert.True(episode.IsArchived);
            Assert.False(episode.IsDownloaded);
            Assert.Null(episode.DownloadedFileName);
            Assert.False(File.Exists(filePath));
            Assert.Null(tracker.Get(episodeId)); // stale Completed snapshot dropped
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task MarkAsPlayed_LockedFile_DefersDeleteButStillArchives()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var service = BuildService(factory, tracker);
        var (episodeId, slug, filePath) = await SeedDownloadedEpisodeAsync(factory);

        try
        {
            // Hold the file open the way the media endpoint does during playback.
            await using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await service.MarkAsPlayedAsync(episodeId, CancellationToken.None);
            }

            var episode = await LoadEpisodeAsync(factory, episodeId);
            // Flags move now so the episode leaves the active list immediately...
            Assert.True(episode.IsPlayed);
            Assert.True(episode.IsArchived);
            // ...but the download pointer survives so the retention sweep can retry.
            Assert.True(episode.IsDownloaded);
            Assert.NotNull(episode.DownloadedFileName);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task MarkAsPlayed_YouTube_IsFlagsOnly()
    {
        using var factory = new TestDbContextFactory();
        var service = BuildService(factory, new DownloadProgressTracker());
        var episodeId = await SeedYouTubeEpisodeAsync(factory);

        await service.MarkAsPlayedAsync(episodeId, CancellationToken.None);

        var episode = await LoadEpisodeAsync(factory, episodeId);
        Assert.True(episode.IsPlayed);
        Assert.True(episode.IsArchived);
    }

    [Fact]
    public async Task Unarchive_FileStillOnDisk_DoesNotRedownload()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var service = BuildService(factory, tracker);
        var (episodeId, slug, filePath) = await SeedDownloadedEpisodeAsync(factory, played: true);

        try
        {
            await service.UnarchiveAsync(episodeId, CancellationToken.None);

            var episode = await LoadEpisodeAsync(factory, episodeId);
            Assert.False(episode.IsPlayed);
            Assert.False(episode.IsArchived);
            // Deferred-deletion leftover: still on disk, so no redundant re-fetch.
            Assert.True(episode.IsDownloaded);
            Assert.NotNull(episode.DownloadedFileName);
            Assert.True(File.Exists(filePath));
            Assert.Null(tracker.Get(episodeId)); // nothing queued
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task Unarchive_FileGone_QueuesRedownload()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var service = BuildService(factory, tracker);
        var (episodeId, slug, filePath) = await SeedDownloadedEpisodeAsync(factory, played: true);

        try
        {
            File.Delete(filePath); // the normal mark-as-played outcome

            await service.UnarchiveAsync(episodeId, CancellationToken.None);

            var episode = await LoadEpisodeAsync(factory, episodeId);
            Assert.False(episode.IsPlayed);
            Assert.False(episode.IsArchived);
            Assert.False(episode.IsDownloaded);
            Assert.Null(episode.DownloadedFileName);
            Assert.Equal(DownloadStatus.Queued, tracker.Get(episodeId)!.Status);
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task Unarchive_YouTube_IsViewOnlyMove()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var service = BuildService(factory, tracker);
        var episodeId = await SeedYouTubeEpisodeAsync(factory, played: true);

        await service.UnarchiveAsync(episodeId, CancellationToken.None);

        var episode = await LoadEpisodeAsync(factory, episodeId);
        Assert.False(episode.IsPlayed);
        Assert.False(episode.IsArchived);
        Assert.Null(tracker.Get(episodeId)); // nothing to download for YouTube
    }

    [Fact]
    public async Task SavePlaybackPosition_Persists()
    {
        using var factory = new TestDbContextFactory();
        var service = BuildService(factory, new DownloadProgressTracker());
        var episodeId = await SeedYouTubeEpisodeAsync(factory);

        await service.SavePlaybackPositionAsync(episodeId, 754, CancellationToken.None);

        var episode = await LoadEpisodeAsync(factory, episodeId);
        Assert.Equal(754, episode.PlaybackPositionSeconds);
    }

    private static EpisodeStateService BuildService(TestDbContextFactory factory, DownloadProgressTracker tracker) =>
        new(factory, new DownloadQueue(tracker), tracker, NullLogger<EpisodeStateService>.Instance);

    /// <summary>Seeds an RSS episode with a real downloaded file under downloads/&lt;slug&gt;/.</summary>
    private static async Task<(int episodeId, string slug, string filePath)> SeedDownloadedEpisodeAsync(
        TestDbContextFactory factory, bool played = false)
    {
        var slug = "state-" + Guid.NewGuid().ToString("N");
        const string fileName = "episode.mp3";

        await using var db = factory.CreateDbContext();
        var episode = new Episode
        {
            DedupKey = "guid:1",
            Title = "Episode One",
            PublishedAtUtc = DateTime.UtcNow,
            EnclosureUrl = "https://cdn.example/ep.mp3",
            EnclosureMediaType = "audio/mpeg",
            IsDownloaded = true,
            DownloadedFileName = fileName,
            IsPlayed = played,
            IsArchived = played,
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

        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, slug);
        Directory.CreateDirectory(feedDir);
        var filePath = Path.Combine(feedDir, fileName);
        await File.WriteAllTextAsync(filePath, "audio bytes");

        return (episode.Id, slug, filePath);
    }

    private static async Task<int> SeedYouTubeEpisodeAsync(TestDbContextFactory factory, bool played = false)
    {
        await using var db = factory.CreateDbContext();
        var episode = new Episode
        {
            DedupKey = "yt:v1",
            Title = "A Video",
            YouTubeVideoId = "v1",
            PublishedAtUtc = DateTime.UtcNow,
            IsPlayed = played,
            IsArchived = played,
            Feed = new Feed
            {
                OriginalUrl = "https://youtube.com/channel/UCx",
                FeedUrl = "https://www.youtube.com/feeds/videos.xml?channel_id=UCx",
                Slug = "yt-" + Guid.NewGuid().ToString("N"),
                Type = FeedType.YouTube,
                DateAddedUtc = DateTime.UtcNow,
            },
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();
        return episode.Id;
    }

    private static async Task<Episode> LoadEpisodeAsync(TestDbContextFactory factory, int episodeId)
    {
        await using var db = factory.CreateDbContext();
        return await db.Episodes.SingleAsync(e => e.Id == episodeId);
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
