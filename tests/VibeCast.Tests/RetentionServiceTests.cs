using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Retention;
using Xunit;

namespace VibeCast.Tests;

// Keep-last-N backstop invariants (CLAUDE.md): caps downloaded files on disk per
// feed, never DB rows (additive model keeps records forever); 0 disables; the
// per-feed override beats the global default; and the sweep retries deletions that
// mark-as-played deferred because the file was locked.
public class RetentionServiceTests
{
    [Fact]
    public async Task KeepLastN_DeletesOldestFiles_ButKeepsDbRows()
    {
        using var factory = new TestDbContextFactory();
        var (feedId, slug, files) = await SeedFeedWithDownloadsAsync(factory, episodeCount: 3, keepLastCount: 2);

        try
        {
            await BuildService(factory).EnforceFeedAsync(feedId, CancellationToken.None);

            // files[0] is newest; only the oldest (index 2) is evicted.
            Assert.True(File.Exists(files[0]));
            Assert.True(File.Exists(files[1]));
            Assert.False(File.Exists(files[2]));

            await using var db = factory.CreateDbContext();
            var episodes = await db.Episodes.OrderByDescending(e => e.PublishedAtUtc).ToListAsync();
            Assert.Equal(3, episodes.Count); // rows survive eviction
            Assert.True(episodes[0].IsDownloaded);
            Assert.True(episodes[1].IsDownloaded);
            Assert.False(episodes[2].IsDownloaded);
            Assert.Null(episodes[2].DownloadedFileName);
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task KeepLastZero_DisablesTheBackstop()
    {
        using var factory = new TestDbContextFactory();
        var (feedId, slug, files) = await SeedFeedWithDownloadsAsync(factory, episodeCount: 3, keepLastCount: 0);

        try
        {
            await BuildService(factory).EnforceFeedAsync(feedId, CancellationToken.None);

            Assert.All(files, f => Assert.True(File.Exists(f)));
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task NullPerFeedOverride_FallsBackToGlobalDefault()
    {
        using var factory = new TestDbContextFactory();
        var (feedId, slug, files) = await SeedFeedWithDownloadsAsync(factory, episodeCount: 3, keepLastCount: null);
        var config = new AppConfig { DefaultKeepLastCount = 1 };

        try
        {
            await BuildService(factory, config).EnforceFeedAsync(feedId, CancellationToken.None);

            Assert.True(File.Exists(files[0]));
            Assert.False(File.Exists(files[1]));
            Assert.False(File.Exists(files[2]));
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task DeferredPlayedDeletion_IsRetried_EvenInsideKeepWindow()
    {
        using var factory = new TestDbContextFactory();
        // Keep window comfortably covers both episodes -- only the played flag
        // should cause the deletion.
        var (feedId, slug, files) = await SeedFeedWithDownloadsAsync(factory, episodeCount: 2, keepLastCount: 100);

        try
        {
            await using (var db = factory.CreateDbContext())
            {
                var newest = await db.Episodes.OrderByDescending(e => e.PublishedAtUtc).FirstAsync();
                newest.IsPlayed = true; // mark-as-played whose delete was deferred (file still on disk)
                newest.IsArchived = true;
                await db.SaveChangesAsync();
            }

            await BuildService(factory).EnforceFeedAsync(feedId, CancellationToken.None);

            Assert.False(File.Exists(files[0])); // played file swept despite being newest
            Assert.True(File.Exists(files[1]));

            await using var verify = factory.CreateDbContext();
            var played = await verify.Episodes.OrderByDescending(e => e.PublishedAtUtc).FirstAsync();
            Assert.False(played.IsDownloaded);
            Assert.Null(played.DownloadedFileName);
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    [Fact]
    public async Task LockedFile_SurvivesSweep_AndKeepsItsDbPointer()
    {
        using var factory = new TestDbContextFactory();
        var (feedId, slug, files) = await SeedFeedWithDownloadsAsync(factory, episodeCount: 2, keepLastCount: 1);

        try
        {
            // Lock the oldest file (the eviction candidate) as the player would.
            await using (new FileStream(files[1], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await BuildService(factory).EnforceFeedAsync(feedId, CancellationToken.None);
            }

            Assert.True(File.Exists(files[1]));

            await using var db = factory.CreateDbContext();
            var oldest = await db.Episodes.OrderBy(e => e.PublishedAtUtc).FirstAsync();
            // Pointer stays intact so the next sweep retries the delete.
            Assert.True(oldest.IsDownloaded);
            Assert.NotNull(oldest.DownloadedFileName);
        }
        finally
        {
            SafeDeleteDir(Path.Combine(AppPaths.DownloadsDirectory, slug));
        }
    }

    private static RetentionService BuildService(TestDbContextFactory factory, AppConfig? config = null) =>
        new(factory, new DownloadProgressTracker(), config ?? new AppConfig(), NullLogger<RetentionService>.Instance);

    /// <summary>
    /// Seeds one RSS feed with <paramref name="episodeCount"/> downloaded episodes and
    /// their files on disk. Returns file paths ordered newest-first (matching the
    /// eviction ordering, which keeps the newest N).
    /// </summary>
    private static async Task<(int feedId, string slug, string[] filesNewestFirst)> SeedFeedWithDownloadsAsync(
        TestDbContextFactory factory, int episodeCount, int? keepLastCount)
    {
        var slug = "retain-" + Guid.NewGuid().ToString("N");
        var feed = new Feed
        {
            OriginalUrl = "https://example/feed",
            FeedUrl = "https://example/feed",
            Slug = slug,
            Title = "Retention Feed",
            Type = FeedType.Rss,
            KeepLastCount = keepLastCount,
            DateAddedUtc = DateTime.UtcNow,
        };

        for (var i = 0; i < episodeCount; i++)
        {
            feed.Episodes.Add(new Episode
            {
                DedupKey = $"guid:{i}",
                Title = $"Episode {i}",
                PublishedAtUtc = DateTime.UtcNow.AddDays(-i), // i=0 newest
                EnclosureUrl = $"https://cdn.example/ep{i}.mp3",
                EnclosureMediaType = "audio/mpeg",
                IsDownloaded = true,
                DownloadedFileName = $"ep{i}.mp3",
            });
        }

        await using (var db = factory.CreateDbContext())
        {
            db.Feeds.Add(feed);
            await db.SaveChangesAsync();
        }

        var feedDir = Path.Combine(AppPaths.DownloadsDirectory, slug);
        Directory.CreateDirectory(feedDir);
        var files = new string[episodeCount];
        for (var i = 0; i < episodeCount; i++)
        {
            files[i] = Path.Combine(feedDir, $"ep{i}.mp3");
            await File.WriteAllTextAsync(files[i], $"audio {i}");
        }

        return (feed.Id, slug, files);
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
