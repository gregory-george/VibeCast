using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeCast.AppHost;

namespace VibeCast.Feeds;

/// <summary>
/// Refreshes all feeds on a recurring timer while the app is running
/// (<see cref="AppConfig.AutoRefreshIntervalMinutes"/>, clamped to 30-180 minutes).
/// The interval is re-read from config before each wait, so a Settings change takes
/// effect on the next cycle without a restart. The launch-time refresh is separate
/// (RefreshOnOpen in HostRunner); the first timed refresh fires one interval after
/// startup, not immediately.
/// </summary>
internal sealed class PeriodicFeedRefreshService(
    IServiceProvider services,
    AppConfig config,
    ILogger<PeriodicFeedRefreshService> logger) : BackgroundService
{
    internal const int MinIntervalMinutes = 30;
    internal const int MaxIntervalMinutes = 180;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Clamp here rather than trusting config.json -- a hand-edited value outside
            // the supported range shouldn't produce a hot refresh loop or a never-firing one.
            var minutes = Math.Clamp(config.AutoRefreshIntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // FeedRefreshService is scoped; a fresh scope per cycle keeps its
                // dependencies' lifetimes correct.
                await using var scope = services.CreateAsyncScope();
                var refreshService = scope.ServiceProvider.GetRequiredService<FeedRefreshService>();
                await refreshService.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Per-feed failures are already recorded on the feed rows; this guards the
                // sweep itself so one bad cycle doesn't kill the timer for the whole run.
                logger.LogError(ex, "Periodic feed refresh failed");
            }
        }
    }
}
