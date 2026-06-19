using System.Collections.Concurrent;

namespace VibeCast.Downloads;

/// <summary>
/// In-memory, session-only view of queue/download state for the UI. Not persisted
/// -- the DB's IsDownloaded/DownloadedFileName columns are the durable record;
/// this is just live progress for the Downloads page and episode detail pane.
/// </summary>
internal sealed class DownloadProgressTracker
{
    private readonly ConcurrentDictionary<int, DownloadProgressSnapshot> snapshots = new();

    public event Action? Changed;

    public void Set(DownloadProgressSnapshot snapshot)
    {
        snapshots[snapshot.EpisodeId] = snapshot;
        Changed?.Invoke();
    }

    public DownloadProgressSnapshot? Get(int episodeId) =>
        snapshots.TryGetValue(episodeId, out var snapshot) ? snapshot : null;

    public IReadOnlyCollection<DownloadProgressSnapshot> GetAll() => snapshots.Values.ToList();

    /// <summary>
    /// Drops a stale "Completed" snapshot once the file it described is gone
    /// (mark-as-played deletion, keep-last-N eviction) so download-status UI falls
    /// back to reading the DB's IsDownloaded column instead of trusting old progress.
    /// </summary>
    public void Clear(int episodeId)
    {
        if (snapshots.TryRemove(episodeId, out _))
        {
            Changed?.Invoke();
        }
    }
}
