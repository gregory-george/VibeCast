using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace VibeCast.AppHost;

internal static class SingleInstance
{
    private const string MutexName = "VibeCast.SingleInstance.Mutex";

    /// <summary>
    /// Acquires the local, non-abandoned single-instance mutex, held for the
    /// full process lifetime. Returns null if another instance already owns it.
    /// </summary>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Reads run.lock and, if the port is actually live, opens a fresh browser
    /// tab against it. run.lock can be stale (prior run crashed without removing
    /// it), so liveness is verified rather than assumed.
    /// </summary>
    public static bool TryRelaunchExisting()
    {
        var port = RunLock.TryRead();
        if (port is null || !IsPortLive(port.Value))
        {
            return false;
        }

        OpenInBrowser(port.Value);
        return true;
    }

    public static bool IsPortLive(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
            return connectTask.Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenInBrowser(int port)
    {
        Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
    }
}
