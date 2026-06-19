using System.Collections.Concurrent;

namespace VibeCast.Downloads;

/// <summary>Lets the UI cancel a single in-flight download without stopping the worker.</summary>
internal sealed class DownloadCancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> activeDownloads = new();

    public CancellationTokenSource Register(int episodeId, CancellationToken linkedTo)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        activeDownloads[episodeId] = cts;
        return cts;
    }

    public void Unregister(int episodeId) => activeDownloads.TryRemove(episodeId, out _);

    public bool TryCancel(int episodeId)
    {
        if (!activeDownloads.TryGetValue(episodeId, out var cts))
        {
            return false;
        }

        cts.Cancel();
        return true;
    }
}
