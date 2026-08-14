using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.Data;
using VibeCast.Downloads;

namespace VibeCast.Episodes;

/// <summary>
/// Mark-as-played and Archive/Unarchive state transitions. Played and archived move
/// together in v1 (tracked as distinct flags for future flexibility, per CLAUDE.md).
/// </summary>
internal sealed class EpisodeStateService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    DownloadQueue downloadQueue,
    DownloadProgressTracker progressTracker,
    DownloadCancellationRegistry cancellationRegistry,
    ILogger<EpisodeStateService> logger)
{
    /// <summary>
    /// For RSS, deletes the downloaded file immediately (CLAUDE.md: mark-as-played is
    /// the trigger, not a later cleanup pass). YouTube has nothing on disk, so it's a
    /// pure flag move. Both move the record to Archive.
    /// </summary>
    public async Task MarkAsPlayedAsync(int episodeId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null)
        {
            return;
        }

        episode.IsPlayed = true;
        episode.IsArchived = true;
        DeleteDownloadedFile(episode);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Bulk mark-as-played over every active episode of one feed. Each episode's queued
    /// or in-flight download is canceled first so the queue isn't left fetching files for
    /// records that just moved to Archive; the per-episode transition is otherwise
    /// identical to <see cref="MarkAsPlayedAsync"/>. Returns how many were marked.
    /// </summary>
    public async Task<int> MarkFeedAsPlayedAsync(int feedId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episodes = await db.Episodes
            .Include(e => e.Feed)
            .Where(e => e.FeedId == feedId && !e.IsArchived)
            .ToListAsync(ct);

        foreach (var episode in episodes)
        {
            CancelDownload(episode.Id);
            episode.IsPlayed = true;
            episode.IsArchived = true;
            DeleteDownloadedFile(episode);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Marked {Count} episode(s) as played for feed {FeedId}", episodes.Count, feedId);
        return episodes.Count;
    }

    /// <summary>
    /// Cancels a download only when one is genuinely live. The registry parks a pending
    /// cancellation for any episode it doesn't find in flight, and a stale entry would
    /// kill an unrelated later download of the same episode (e.g. after Unarchive).
    /// </summary>
    private void CancelDownload(int episodeId)
    {
        if (progressTracker.Get(episodeId) is { Status: DownloadStatus.Queued or DownloadStatus.Downloading })
        {
            cancellationRegistry.TryCancel(episodeId);
        }
    }

    /// <summary>
    /// Deletes the RSS enclosure of an episode being marked played (YouTube has nothing
    /// on disk). A locked file -- almost always one still open in the player -- is left
    /// alone with IsDownloaded/DownloadedFileName intact so the retention sweep retries
    /// the delete once playback releases it; the played/archived flags still move now so
    /// the episode leaves the active list immediately. The same sweep also catches a
    /// download that lands moments after cancellation, since it works off IsPlayed +
    /// IsDownloaded rather than anything in memory.
    /// </summary>
    private void DeleteDownloadedFile(Episode episode)
    {
        if (episode.EnclosureUrl is null || episode.DownloadedFileName is null)
        {
            return;
        }

        var filePath = DownloadFileStore.PathFor(episode.Feed.Slug, episode.DownloadedFileName);
        if (DownloadFileStore.TryDelete(filePath, logger))
        {
            episode.IsDownloaded = false;
            episode.DownloadedFileName = null;
            progressTracker.Clear(episode.Id);
        }
    }

    public async Task UnarchiveAsync(int episodeId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.Include(e => e.Feed).FirstOrDefaultAsync(e => e.Id == episodeId, ct);
        if (episode is null)
        {
            return;
        }

        episode.IsPlayed = false;
        episode.IsArchived = false;

        // RSS: re-download only if the file is actually missing. Mark-as-played
        // normally deletes it, but a deferred (locked-file) deletion can leave it on
        // disk -- skip the redundant re-fetch when it's still there.
        if (episode.EnclosureUrl is not null)
        {
            var filePath = episode.DownloadedFileName is null
                ? null
                : DownloadFileStore.PathFor(episode.Feed.Slug, episode.DownloadedFileName);

            if (filePath is null || !File.Exists(filePath))
            {
                episode.IsDownloaded = false;
                episode.DownloadedFileName = null;
                await db.SaveChangesAsync(ct);

                var feedTitle = episode.Feed.Title ?? episode.Feed.OriginalUrl;
                downloadQueue.Enqueue(episode.Id, episode.Title, feedTitle);
                return;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Persists resume position. Meaningless once the file is gone (mark-as-played
    /// deletes the RSS file), but harmless to call -- the row simply stops being read
    /// back by anything once IsPlayed flips.
    /// </summary>
    public async Task SavePlaybackPositionAsync(int episodeId, int positionSeconds, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.FindAsync([episodeId], ct);
        if (episode is null)
        {
            return;
        }

        episode.PlaybackPositionSeconds = positionSeconds;
        await db.SaveChangesAsync(ct);
    }
}
