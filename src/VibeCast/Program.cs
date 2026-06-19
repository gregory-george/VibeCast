using VibeCast.AppHost;

namespace VibeCast;

internal static class Program
{
    /// <summary>
    /// The WinForms message loop owns this main STA thread (required for the
    /// future NotifyIcon); the Blazor/Kestrel IHost runs on a background thread.
    /// Shutdown is bridged both ways: the host's WaitForShutdownAsync completing
    /// (success or failure) calls ExitThread() back on this thread, and a future
    /// tray Quit action (Phase 3) will call StopApplication() on the host.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = SingleInstance.TryAcquire();
        if (mutex is null)
        {
            SingleInstance.TryRelaunchExisting();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var uiSyncContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiSyncContext);

        var trayContext = new TrayApplicationContext();

        // Phase 0 only: no tray Quit yet, so Ctrl+C in the dev console window is the
        // way to trigger a graceful shutdown. Cancel=true stops the runtime from
        // killing the process immediately, letting StopApplication() finish cleanly
        // (run.lock removal, WAL checkpoint). Replaced by tray Quit in Phase 3/6.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            trayContext.HostLifetime?.StopApplication();
        };

        var hostThread = new Thread(() => HostRunner.Run(args, uiSyncContext, trayContext))
        {
            IsBackground = true,
            Name = "VibeCast.Host",
        };
        hostThread.Start();

        Application.Run(trayContext);
    }
}
