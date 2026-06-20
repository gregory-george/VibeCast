using VibeCast.Downloads;

namespace VibeCast.AppHost;

/// <summary>
/// Hosts the WinForms message loop required to keep this process's STA thread alive
/// alongside the background <see cref="WebApplication"/>, and owns the tray
/// NotifyIcon: running indicator + Reopen UI + Quit (clean StopApplication(), with a
/// confirm prompt if a download is mid-flight -- see CLAUDE.md "finish, then exit").
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>How long Quit waits for graceful shutdown before forcing the process down.</summary>
    private const int ForceQuitTimeoutSeconds = 10;

    private readonly NotifyIcon notifyIcon;
    private CancellationTokenSource? forceQuitWatchdog;

    public TrayApplicationContext()
    {
        notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VibeCast (starting...)",
            Visible = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Reopen UI", null, (_, _) => ReopenUi());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.DoubleClick += (_, _) => ReopenUi();
    }

    /// <summary>
    /// Set once the host has started, so tray Quit can call StopApplication() on it.
    /// </summary>
    public IHostApplicationLifetime? HostLifetime { get; set; }

    /// <summary>Set once the host has started, so Quit can check for in-flight downloads.</summary>
    public DownloadProgressTracker? DownloadTracker { get; set; }

    /// <summary>Set once the host has started, so a confirmed Quit can cancel in-flight downloads cleanly.</summary>
    public DownloadCancellationRegistry? CancellationRegistry { get; set; }

    public int? LivePort { get; private set; }

    public void ShowTrayIcon(int port)
    {
        LivePort = port;
        notifyIcon.Text = $"VibeCast - running on port {port}";
        notifyIcon.Visible = true;
    }

    private void ReopenUi()
    {
        if (LivePort is { } port)
        {
            SingleInstance.OpenInBrowser(port);
        }
    }

    /// <summary>
    /// First-run discoverability (CLAUDE.md §2): must run on this STA thread
    /// (MessageBox.Show needs a live message loop), so HostRunner posts here via
    /// the WinForms SynchronizationContext rather than calling it directly.
    /// </summary>
    public void OfferDesktopShortcut()
    {
        var result = MessageBox.Show(
            "Create a desktop shortcut to VibeCast? Portable apps don't get a Start menu entry, so this is the easiest way back in (you can pin it to the taskbar from there too).",
            "VibeCast",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (result == DialogResult.Yes)
        {
            DesktopShortcut.TryCreate();
        }
    }

    private void Quit()
    {
        if (forceQuitWatchdog is not null)
        {
            // Already quitting (e.g. double-clicked Quit) -- let the in-flight attempt run.
            return;
        }

        if (DownloadTracker is { HasActiveDownloads: true })
        {
            var result = MessageBox.Show(
                "A download is still in progress. Quitting will cancel it -- it resumes automatically next launch.\n\nQuit anyway?",
                "VibeCast",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (result != DialogResult.Yes)
            {
                return;
            }

            CancelActiveDownloads();
        }

        if (HostLifetime is null)
        {
            // Host never started (or already failed) -- nothing to shut down gracefully.
            ForceQuit();
            return;
        }

        HostLifetime.StopApplication();
        ArmForceQuitWatchdog();
    }

    /// <summary>
    /// Backstop for "finish, then exit" (CLAUDE.md): if graceful shutdown hangs -- a
    /// hosted service overrunning, a stuck HTTP read, a blocked WAL checkpoint -- the user
    /// asked to Quit and the process must actually go away. Cancelled by ExitThreadCore
    /// once the normal shutdown path completes, so this never fires on the happy path.
    /// </summary>
    private void ArmForceQuitWatchdog()
    {
        var cts = new CancellationTokenSource();
        forceQuitWatchdog = cts;

        _ = Task.Delay(TimeSpan.FromSeconds(ForceQuitTimeoutSeconds), cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    ForceQuit();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Hard close: skips the WAL checkpoint and leaves a stale run.lock, both of which are
    /// already safe by design -- SQLite WAL recovers on next open, and the launcher already
    /// treats run.lock as potentially stale and validates the port before reuse.
    /// </summary>
    private void ForceQuit()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        Environment.Exit(0);
    }

    private void CancelActiveDownloads()
    {
        if (DownloadTracker is null || CancellationRegistry is null)
        {
            return;
        }

        foreach (var snapshot in DownloadTracker.GetAll())
        {
            if (snapshot.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
            {
                CancellationRegistry.TryCancel(snapshot.EpisodeId);
            }
        }
    }

    protected override void ExitThreadCore()
    {
        // Graceful shutdown completed -- disarm the force-quit fallback.
        forceQuitWatchdog?.Cancel();

        // Hide immediately so no ghost icon lingers in the tray after exit.
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
