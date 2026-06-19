using VibeCast.AppHost;
using VibeCast.Downloads;

namespace VibeCast.Shutdown;

/// <summary>
/// "Finish, then exit" (CLAUDE.md): the backend lives only while the app is in use.
/// Watches <see cref="CircuitTracker"/> and <see cref="DownloadProgressTracker"/>; once
/// circuits hit zero, waits out a grace window (survives a refresh/reconnect blip and
/// sleep/wake churn) and then -- only once active downloads have also reached zero --
/// calls <see cref="IHostApplicationLifetime.StopApplication"/>. Never evaluates at
/// startup before any circuit has ever connected, so a slow first browser launch can't
/// race the app into shutting itself down before anyone opened it.
/// </summary>
internal sealed class ShutdownCoordinatorService(
    CircuitTracker circuitTracker,
    DownloadProgressTracker progressTracker,
    IHostApplicationLifetime lifetime,
    AppConfig config,
    ILogger<ShutdownCoordinatorService> logger) : BackgroundService
{
    private readonly TimeSpan graceWindow = TimeSpan.FromSeconds(Math.Max(1, config.GraceWindowSeconds));
    private readonly Lock gate = new();
    private CancellationTokenSource? graceCts;
    private bool graceElapsed;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        circuitTracker.Changed += Evaluate;
        progressTracker.Changed += Evaluate;
        stoppingToken.Register(() =>
        {
            circuitTracker.Changed -= Evaluate;
            progressTracker.Changed -= Evaluate;
        });
        return Task.CompletedTask;
    }

    private void Evaluate()
    {
        lock (gate)
        {
            if (circuitTracker.ActiveCircuits > 0)
            {
                graceCts?.Cancel();
                graceCts = null;
                graceElapsed = false;
                return;
            }

            if (graceElapsed)
            {
                if (!progressTracker.HasActiveDownloads)
                {
                    logger.LogInformation("Zero circuits, zero active downloads, grace window elapsed -- shutting down.");
                    lifetime.StopApplication();
                }

                return;
            }

            graceCts ??= StartGraceWindow();
        }
    }

    private CancellationTokenSource StartGraceWindow()
    {
        var cts = new CancellationTokenSource();
        _ = WaitOutGraceWindowAsync(cts.Token);
        return cts;
    }

    private async Task WaitOutGraceWindowAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(graceWindow, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (gate)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            graceElapsed = true;
        }

        Evaluate();
    }
}
