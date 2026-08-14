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
    public async Task MarkFeedAsPlayed_ArchivesActiveEpisodesAndDeletesTheirFiles()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var service = BuildService(factory, tracker);
        var (feedId, slug, episodeIds, filePath) = await SeedFeedAsync(factory);

        try
        {
            var marked = await service.MarkFeedAsPlayedAsync(feedId, CancellationToken.None);

            Assert.Equal(episodeIds.Count, marked);
            foreach (var episodeId in episodeIds)
            {
                var episode = await LoadEpisodeAsync(factory, episodeId);
                Assert.True(episode.IsPlayed);
                Assert.True(episode.IsArchived);
                Assert.False(episode.IsDownloaded);
                Assert.Null(episode.DownloadedFileName);
            }

            Assert.False(File.Exists(filePath));
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task MarkFeedAsPlayed_CancelsInFlightDownload()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var registry = new DownloadCancellationRegistry(tracker);
        var service = BuildService(factory, tracker, registry);
        var (feedId, slug, episodeIds, _) = await SeedFeedAsync(factory);
        var downloadingId = episodeIds[0];
        var queuedId = episodeIds[1];

        try
        {
            // One episode mid-flight (the worker has registered it), one still queued.
            using var downloadCts = registry.Register(downloadingId, CancellationToken.None);
            tracker.Set(new DownloadProgressSnapshot(downloadingId, "Ep", "Feed", DownloadStatus.Downloading, 1, 10, null));
            tracker.Set(new DownloadProgressSnapshot(queuedId, "Ep", "Feed", DownloadStatus.Queued, 0, null, null));

            await service.MarkFeedAsPlayedAsync(feedId, CancellationToken.None);

            Assert.True(downloadCts.IsCancellationRequested);
            Assert.Equal(DownloadStatus.Canceled, tracker.Get(queuedId)!.Status);
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task MarkFeedAsPlayed_DoesNotCancelALaterUnrelatedDownload()
    {
        using var factory = new TestDbContextFactory();
        var tracker = new DownloadProgressTracker();
        var registry = new DownloadCancellationRegistry(tracker);
        var service = BuildService(factory, tracker, registry);
        var (feedId, slug, episodeIds, _) = await SeedFeedAsync(factory);

        try
        {
            // No live download for any episode, so nothing may be parked in the registry:
            // a stale pending cancellation would kill the re-download an Unarchive queues.
            await service.MarkFeedAsPlayedAsync(feedId, CancellationToken.None);

            using var laterCts = registry.Register(episodeIds[0], CancellationToken.None);
            Assert.False(laterCts.IsCancellationRequested);
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
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
        BuildService(factory, tracker, new DownloadCancellationRegistry(tracker));

    private static EpisodeStateService BuildService(
        TestDbContextFactory factory, DownloadProgressTracker tracker, DownloadCancellationRegistry registry) =>
        new(factory, new DownloadQueue(tracker), tracker, registry, NullLogger<EpisodeStateService>.Instance);

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

    /// <summary>
    /// Seeds an RSS feed with three active episodes (the first downloaded, with a real
    /// file on disk) plus one already-archived episode the bulk pass must ignore.
    /// </summary>
    private static async Task<(int feedId, string slug, List<int> activeEpisodeIds, string filePath)> SeedFeedAsync(
        TestDbContextFactory factory)
    {
        var slug = "bulk-" + Guid.NewGuid().ToString("N");
        const string fileName = "episode.mp3";

        await using var db = factory.CreateDbContext();
        var feed = new Feed
        {
            OriginalUrl = "https://example/feed",
            FeedUrl = "https://example/feed",
            Slug = slug,
            Title = "Test Feed",
            Type = FeedType.Rss,
            DateAddedUtc = DateTime.UtcNow,
        };

        var episodes = Enumerable.Range(0, 4).Select(i => new Episode
        {
            Feed = feed,
            DedupKey = $"guid:{i}",
            Title = $"Episode {i}",
            PublishedAtUtc = DateTime.UtcNow.AddDays(-i),
            EnclosureUrl = $"https://cdn.example/ep{i}.mp3",
            EnclosureMediaType = "audio/mpeg",
            IsDownloaded = i == 0,
            DownloadedFileName = i == 0 ? fileName : null,
            IsPlayed = i == 3,
            IsArchived = i == 3,
        }).ToList();

        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync();

        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, slug);
        Directory.CreateDirectory(feedDir);
        var filePath = Path.Combine(feedDir, fileName);
        await File.WriteAllTextAsync(filePath, "audio bytes");

        return (feed.Id, slug, episodes.Take(3).Select(e => e.Id).ToList(), filePath);
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
