using System.Globalization;

namespace VibeCast.AppHost;

/// <summary>
/// The truly-live port for this run, as opposed to config.json's sticky preference.
/// May be stale if a prior run crashed without cleaning up; callers must verify
/// liveness (see <see cref="SingleInstance.IsPortLive"/>) before trusting it.
/// </summary>
internal static class RunLock
{
    public static void Write(int port)
    {
        File.WriteAllText(AppPaths.RunLockFile, port.ToString(CultureInfo.InvariantCulture));
    }

    public static void Delete()
    {
        try
        {
            File.Delete(AppPaths.RunLockFile);
        }
        catch (IOException)
        {
        }
    }

    public static int? TryRead()
    {
        try
        {
            if (!File.Exists(AppPaths.RunLockFile))
            {
                return null;
            }

            var text = File.ReadAllText(AppPaths.RunLockFile).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                ? port
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
