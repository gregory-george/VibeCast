namespace VibeCast.Downloads;

/// <summary>Lets the UI cancel a single download without stopping the worker.</summary>
/// <remarks>
/// Handles both in-flight and still-queued items. A queued item has no
/// <see cref="CancellationTokenSource"/> yet (the worker creates it on dequeue via
/// <see cref="Register"/>), so a cancel request that arrives first is parked in
/// <c>pendingCancellations</c> and applied the instant the worker registers it.
/// </remarks>
internal sealed class DownloadCancellationRegistry(DownloadProgressTracker progressTracker)
{
    private readonly object gate = new();
    private readonly Dictionary<int, CancellationTokenSource> activeDownloads = new();
    private readonly HashSet<int> pendingCancellations = new();

    public CancellationTokenSource Register(int episodeId, CancellationToken linkedTo)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        bool cancelNow;
        lock (gate)
        {
            activeDownloads[episodeId] = cts;
            // A cancel that landed while this episode was still queued applies now.
            cancelNow = pendingCancellations.Remove(episodeId);
        }

        if (cancelNow)
        {
            cts.Cancel();
        }

        return cts;
    }

    public void Unregister(int episodeId)
    {
        lock (gate)
        {
            activeDownloads.Remove(episodeId);
            pendingCancellations.Remove(episodeId);
        }
    }

    public bool TryCancel(int episodeId)
    {
        CancellationTokenSource? cts;
        lock (gate)
        {
            if (!activeDownloads.TryGetValue(episodeId, out cts))
            {
                // Not yet dequeued: park the request so Register() honors it...
                pendingCancellations.Add(episodeId);

                // ...and reflect the cancel in the UI now rather than waiting for a
                // worker to dequeue it (with a backlog that could be a while).
                if (progressTracker.Get(episodeId) is { } snapshot)
                {
                    progressTracker.Set(snapshot with { Status = DownloadStatus.Canceled, TotalBytes = null });
                }

                return true;
            }
        }

        // Cancel outside the lock; Cancel() runs token callbacks synchronously.
        cts.Cancel();
        return true;
    }
}
