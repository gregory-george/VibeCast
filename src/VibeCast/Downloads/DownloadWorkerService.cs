using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeCast.AppHost;

namespace VibeCast.Downloads;

/// <summary>
/// Background worker(s) draining the download queue. Runs
/// <see cref="AppConfig.ConcurrentDownloadLimit"/> parallel consumers off the same
/// channel (multi-consumer is safe -- <see cref="EpisodeDownloader"/> holds no
/// mutable state and the channel itself permits concurrent readers); read once at
/// startup, so a change takes effect on next launch.
/// </summary>
internal sealed class DownloadWorkerService(
    DownloadQueue queue,
    DownloadCancellationRegistry cancellationRegistry,
    EpisodeDownloader downloader,
    AppConfig config,
    ILogger<DownloadWorkerService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = Math.Max(1, config.ConcurrentDownloadLimit);
        var workers = Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync(stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
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
