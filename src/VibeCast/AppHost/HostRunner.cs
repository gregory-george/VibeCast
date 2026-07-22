using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VibeCast.Data;
using VibeCast.Downloads;
using VibeCast.Episodes;
using VibeCast.Feeds;
using VibeCast.Logging;
using VibeCast.Playback;
using VibeCast.Retention;
using VibeCast.Shutdown;

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
            RunAsync(args, uiSyncContext, trayContext).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // No DI container to resolve ILogger from if startup itself failed
            // (e.g. every port attempt exhausted) -- write directly.
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            using var bootstrapLogging = new FileLoggerProvider(AppPaths.LogsDirectory);
            bootstrapLogging.CreateLogger(nameof(HostRunner)).LogCritical(ex, "VibeCast host failed to start");
        }
        finally
        {
            uiSyncContext.Post(_ => trayContext.ExitThread(), null);
        }
    }

    private static async Task RunAsync(string[] args, SynchronizationContext uiSyncContext, TrayApplicationContext trayContext)
    {
        Directory.CreateDirectory(AppPaths.DownloadsDirectory);
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        // Cap the logs folder by age. Backups are capped by count (DatabaseLifecycle), but
        // the daily logs would otherwise accumulate forever in the app's own folder.
        LogRetention.PruneOldLogs(AppPaths.LogsDirectory);

        // config.json not existing yet is the real "first run" signal -- AppConfig.Load()
        // always returns a fresh instance with HasOfferedDesktopShortcut = false either way.
        var isFirstRun = !File.Exists(AppPaths.ConfigFile);
        var config = AppConfig.Load();
        var preferredPort = config.PreferredPort ?? PortBinder.DefaultPort;

        DatabaseLifecycle.BackupBeforeMigration();

        var (app, port) = await PortBinder.BuildAndBindAsync(args, preferredPort, builder => ConfigureServices(builder, config));

        await DatabaseLifecycle.MigrateAsync(app.Services);

        // Two-way port persistence: run.lock is the truly-live port for this run
        // (what a second instance reads); config.json is the sticky preference.
        RunLock.Write(port);
        config.PreferredPort = port;
        if (isFirstRun)
        {
            config.HasOfferedDesktopShortcut = true;
        }

        config.Save();

        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VibeCast.Startup")
            .LogInformation("VibeCast listening on port {Port}", port);

        trayContext.HostLifetime = app.Lifetime;
        trayContext.DownloadTracker = app.Services.GetRequiredService<DownloadProgressTracker>();
        trayContext.CancellationRegistry = app.Services.GetRequiredService<DownloadCancellationRegistry>();
        if (config.TrayEnabled)
        {
            // NotifyIcon creates its native window the first time Visible is set, and
            // that window needs to belong to the WinForms STA/message-loop thread, not
            // this background host thread -- otherwise the icon silently never shows or
            // never responds to clicks (NotifyIcon is a Component, not a Control, so
            // there's no InvokeRequired guard to catch the mistake).
            uiSyncContext.Post(_ => trayContext.ShowTrayIcon(port), null);
        }

        if (isFirstRun)
        {
            // Must run on the WinForms STA thread (MessageBox needs a live message
            // loop); RunAsync itself runs on the background host thread.
            uiSyncContext.Post(_ => trayContext.OfferDesktopShortcut(), null);
        }

        SingleInstance.OpenInBrowser(port);

        // Resume downloads a prior run left incomplete (the tray Quit dialog promises
        // in-flight downloads "resume automatically next launch"). Fire-and-forget so
        // the UI isn't blocked; tied to ApplicationStopping so it doesn't outlive the host.
        _ = RunStartupDownloadSweepAsync(app.Services, app.Lifetime.ApplicationStopping);

        // Refresh-on-open: fire-and-forget so the UI isn't blocked on network
        // calls. Tied to ApplicationStopping so it doesn't outlive the host.
        if (config.RefreshOnOpen)
        {
            _ = RunStartupRefreshAsync(app.Services, app.Lifetime.ApplicationStopping);
        }

        await app.WaitForShutdownAsync();

        await RunShutdownRetentionCleanupAsync(app.Services);
        await DatabaseLifecycle.CheckpointAsync(app.Services);
        RunLock.Delete();
    }

    private static async Task RunShutdownRetentionCleanupAsync(IServiceProvider services)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var retentionService = scope.ServiceProvider.GetRequiredService<RetentionService>();
            await retentionService.EnforceAllFeedsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<RetentionService>>().LogError(ex, "Shutdown retention cleanup failed");
        }
    }

    /// <summary>
    /// Re-queues RSS enclosures that should be on disk but aren't -- i.e. auto-downloads
    /// interrupted by a prior exit (their .partial file, if any, resumes via Range).
    /// Uses the same <see cref="AutoDownloadGate"/> as ingest as the single source of
    /// truth for "should auto-download", so an item aged past the cutoff stays skipped,
    /// consistent with the documented behavior. Enqueue only touches an in-memory channel,
    /// so this returns quickly; the actual downloading happens on the worker.
    /// </summary>
    private static async Task RunStartupDownloadSweepAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var downloadQueue = scope.ServiceProvider.GetRequiredService<DownloadQueue>();

            await using var db = await factory.CreateDbContextAsync(ct);
            var pending = await db.Episodes
                .Include(e => e.Feed)
                .Where(e => e.EnclosureUrl != null && !e.IsDownloaded && !e.IsArchived && !e.IsPlayed && e.Feed.AutoDownloadEnabled)
                .ToListAsync(ct);

            foreach (var episode in pending)
            {
                if (AutoDownloadGate.ShouldAutoDownload(episode.Feed, episode))
                {
                    var feedTitle = episode.Feed.Title ?? episode.Feed.OriginalUrl;
                    downloadQueue.Enqueue(episode.Id, episode.Title, feedTitle);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<EpisodeDownloader>>().LogError(ex, "Startup download sweep failed");
        }
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
            services.GetRequiredService<ILogger<FeedRefreshService>>().LogError(ex, "Startup feed refresh failed");
        }
    }

    private static void ConfigureServices(WebApplicationBuilder builder, AppConfig config)
    {
        // No console window (OutputType=WinExe) and no %AppData% writes allowed, so
        // logs/vibecast-YYYYMMDD.log (CLAUDE.md) is the only durable diagnostic trail.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory));
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddSingleton<CircuitTracker>();
        builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, TrackingCircuitHandler>();
        builder.Services.AddHostedService<ShutdownCoordinatorService>();
        builder.Services.AddHostedService<PeriodicFeedRefreshService>();

        // Same mutable instance RunAsync persists run.lock/port through, so a
        // settings toggle saved from the UI doesn't clobber the live-bound port.
        builder.Services.AddSingleton(config);

        builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={AppPaths.DatabaseFile};Default Timeout=30"));

        builder.Services.AddHttpClient<FeedFetcher>(ConfigureHttpClient);
        builder.Services.AddHttpClient<YouTubeChannelResolver>(ConfigureHttpClient);
        builder.Services.AddHttpClient<FeedArtworkService>(ConfigureHttpClient);
        builder.Services.AddHttpClient<YouTubeDurationService>(ConfigureHttpClient);

        // The downloader must NOT inherit the 30s HttpClient.Timeout: that timeout also
        // bounds the response *body* read, so it silently aborts any enclosure that takes
        // longer than 30s to stream (large videos, slow links). Per-download cancellation
        // is handled explicitly via DownloadCancellationRegistry; only the connect phase
        // is bounded, via the primary handler's ConnectTimeout.
        builder.Services.AddHttpClient<EpisodeDownloader>(ConfigureDownloadHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
            });

        builder.Services.AddScoped<FeedSubscriptionService>();
        builder.Services.AddScoped<FeedRefreshService>();
        builder.Services.AddScoped<EpisodeStateService>();
        builder.Services.AddScoped<PlaybackService>();
        builder.Services.AddScoped<RetentionService>();
        builder.Services.AddScoped<Opml.OpmlService>();
        builder.Services.AddSingleton<ShowNotesSanitizer>();

        builder.Services.AddSingleton<DownloadProgressTracker>();
        builder.Services.AddSingleton<DownloadQueue>();
        builder.Services.AddSingleton<DownloadCancellationRegistry>();
        builder.Services.AddHostedService<DownloadWorkerService>();
    }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    private static void ConfigureDownloadHttpClient(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }
}
