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

        if (episode.EnclosureUrl is not null && episode.DownloadedFileName is not null)
        {
            var filePath = DownloadFileStore.PathFor(episode.Feed.Slug, episode.DownloadedFileName);
            if (DownloadFileStore.TryDelete(filePath, logger))
            {
                episode.IsDownloaded = false;
                episode.DownloadedFileName = null;
                progressTracker.Clear(episodeId);
            }
            // Otherwise the file is locked (almost always still open in the player).
            // Leave IsDownloaded/DownloadedFileName intact so the retention sweep retries
            // the delete once playback releases it; the played/archived flags still move
            // now so the episode leaves the active list immediately.
        }

        await db.SaveChangesAsync(ct);
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

        // RSS: re-download only if the file is actually missing (mark-as-played
        // doesn't delete it yet in this phase -- see Phase 5 -- so this avoids a
        // redundant re-fetch of a file that's still sitting on disk).
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
    /// Persists resume position. Meaningless once the file is gone (Phase 5 deletes
    /// the RSS file on mark-as-played), but harmless to call -- the row simply stops
    /// being read back by anything once IsPlayed flips.
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
