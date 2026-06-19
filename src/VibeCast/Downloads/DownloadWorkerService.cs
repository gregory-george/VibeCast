using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VibeCast.Downloads;

/// <summary>
/// Single-consumer background worker draining the download queue. Processes one
/// download at a time -- the per-feed/global concurrency setting (Phase 7) can
/// raise this later; sequential is the simplest correct Phase 3 baseline.
/// </summary>
internal sealed class DownloadWorkerService(
    DownloadQueue queue,
    DownloadCancellationRegistry cancellationRegistry,
    EpisodeDownloader downloader,
    ILogger<DownloadWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            using var episodeCts = cancellationRegistry.Register(request.EpisodeId, stoppingToken);
            try
            {
                await downloader.DownloadAsync(request.EpisodeId, episodeCts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down; let the foreach loop exit on its own.
            }
            catch (Exception ex)
            {
                // EpisodeDownloader already catches and records its own failures;
                // this is a last-resort guard so one bad download can't kill the worker.
                logger.LogError(ex, "Unhandled error downloading episode {EpisodeId}", request.EpisodeId);
            }
            finally
            {
                cancellationRegistry.Unregister(request.EpisodeId);
            }
        }
    }
}
