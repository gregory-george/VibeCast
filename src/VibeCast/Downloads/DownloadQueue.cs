using System.Threading.Channels;

namespace VibeCast.Downloads;

/// <summary>Channel-based background download queue, per CLAUDE.md.</summary>
internal sealed class DownloadQueue(DownloadProgressTracker progressTracker)
{
    private readonly Channel<DownloadRequest> channel = Channel.CreateUnbounded<DownloadRequest>();

    public void Enqueue(int episodeId, string episodeTitle, string feedTitle)
    {
        progressTracker.Set(new DownloadProgressSnapshot(episodeId, episodeTitle, feedTitle, DownloadStatus.Queued, 0, null, null));
        channel.Writer.TryWrite(new DownloadRequest(episodeId));
    }

    public IAsyncEnumerable<DownloadRequest> ReadAllAsync(CancellationToken ct) =>
        channel.Reader.ReadAllAsync(ct);
}
