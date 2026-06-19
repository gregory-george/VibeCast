namespace VibeCast.AppHost;

/// <summary>
/// Hosts the WinForms message loop required to keep this process's STA thread alive
/// alongside the background <see cref="WebApplication"/>, and owns the tray
/// NotifyIcon. Minimal Phase 3 tray: running indicator + Reopen UI + Quit. The
/// confirm-if-download-mid-flight prompt and finish-then-exit circuit/download
/// gating land in Phase 6 -- Quit here is a direct, un-gated StopApplication().
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon notifyIcon;

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
        menu.Items.Add("Quit", null, (_, _) => HostLifetime?.StopApplication());
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.DoubleClick += (_, _) => ReopenUi();
    }

    /// <summary>
    /// Set once the host has started, so tray Quit can call StopApplication() on it.
    /// </summary>
    public IHostApplicationLifetime? HostLifetime { get; set; }

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

    protected override void ExitThreadCore()
    {
        // Hide immediately so no ghost icon lingers in the tray after exit.
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
