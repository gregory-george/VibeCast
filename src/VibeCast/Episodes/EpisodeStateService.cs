using Microsoft.EntityFrameworkCore;
using VibeCast.AppHost;
using VibeCast.Data;
using VibeCast.Downloads;

namespace VibeCast.Episodes;

/// <summary>
/// Mark-as-played and Archive/Unarchive state transitions. Played and archived move
/// together in v1 (tracked as distinct flags for future flexibility, per CLAUDE.md).
/// RSS file deletion on mark-as-played is wired in Phase 5 alongside the rest of
/// the retention policy (keep-last-N, cleanup-on-refresh/shutdown) -- this phase
/// only flips the state flags there. Unarchive's re-download (this phase's explicit
/// scope) is wired below.
/// </summary>
internal sealed class EpisodeStateService(IDbContextFactory<AppDbContext> dbContextFactory, DownloadQueue downloadQueue)
{
    public async Task MarkAsPlayedAsync(int episodeId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var episode = await db.Episodes.FindAsync([episodeId], ct);
        if (episode is null)
        {
            return;
        }

        episode.IsPlayed = true;
        episode.IsArchived = true;
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
                : Path.Combine(AppPaths.DownloadsDirectory, episode.Feed.Slug, episode.DownloadedFileName);

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
}
