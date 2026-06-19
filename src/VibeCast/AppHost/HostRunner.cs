using Microsoft.EntityFrameworkCore;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Episodes;
using VibeCast.Feeds;
using VibeCast.Playback;

namespace VibeCast.AppHost;

/// <summary>
/// Runs on the background thread (see Program.Main): builds and binds the
/// Blazor/Kestrel host, migrates the database, persists the live port, opens the
/// browser, then blocks until shutdown. Always hands control back to the WinForms
/// message loop on the UI thread via <paramref name="uiSyncContext"/> when done,
/// success or failure, so the process doesn't hang with a dead host and a live
/// message pump.
/// </summary>
internal static class HostRunner
{
    public static void Run(string[] args, SynchronizationContext uiSyncContext, TrayApplicationContext trayContext)
    {
        try
        {
            RunAsync(args, trayContext).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Structured logging arrives in a later phase; surface fatal startup
            // failures loudly for now rather than failing silently.
            Console.Error.WriteLine($"VibeCast host failed: {ex}");
        }
        finally
        {
            uiSyncContext.Post(_ => trayContext.ExitThread(), null);
        }
    }

    private static async Task RunAsync(string[] args, TrayApplicationContext trayContext)
    {
        Directory.CreateDirectory(AppPaths.DownloadsDirectory);
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        var config = AppConfig.Load();
        var preferredPort = config.PreferredPort ?? PortBinder.DefaultPort;

        DatabaseLifecycle.BackupBeforeMigration();

        var (app, port) = await PortBinder.BuildAndBindAsync(args, preferredPort, ConfigureServices);

        await DatabaseLifecycle.MigrateAsync(app.Services);

        // Two-way port persistence: run.lock is the truly-live port for this run
        // (what a second instance reads); config.json is the sticky preference.
        RunLock.Write(port);
        config.PreferredPort = port;
        config.Save();

        trayContext.HostLifetime = app.Lifetime;
        trayContext.ShowTrayIcon(port);

        SingleInstance.OpenInBrowser(port);

        // Refresh-on-open: fire-and-forget so the UI isn't blocked on network
        // calls. Tied to ApplicationStopping so it doesn't outlive the host.
        _ = RunStartupRefreshAsync(app.Services, app.Lifetime.ApplicationStopping);

        await app.WaitForShutdownAsync();

        await DatabaseLifecycle.CheckpointAsync(app.Services);
        RunLock.Delete();
    }

    private static async Task RunStartupRefreshAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var refreshService = scope.ServiceProvider.GetRequiredService<FeedRefreshService>();
            await refreshService.RefreshAllAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup feed refresh failed: {ex}");
        }
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={AppPaths.DatabaseFile};Default Timeout=30"));

        builder.Services.AddHttpClient<FeedFetcher>(ConfigureHttpClient);
        builder.Services.AddHttpClient<YouTubeChannelResolver>(ConfigureHttpClient);
        builder.Services.AddHttpClient<EpisodeDownloader>(ConfigureHttpClient);

        builder.Services.AddScoped<FeedSubscriptionService>();
        builder.Services.AddScoped<FeedRefreshService>();
        builder.Services.AddScoped<EpisodeStateService>();
        builder.Services.AddScoped<PlaybackService>();
        builder.Services.AddSingleton<ShowNotesSanitizer>();

        builder.Services.AddSingleton<DownloadProgressTracker>();
        builder.Services.AddSingleton<DownloadQueue>();
        builder.Services.AddSingleton<DownloadCancellationRegistry>();
        builder.Services.AddHostedService<DownloadWorkerService>();
    }

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }
}
