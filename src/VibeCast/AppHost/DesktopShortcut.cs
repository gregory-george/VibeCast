namespace VibeCast.AppHost;

/// <summary>
/// First-run discoverability (CLAUDE.md §2): portable means no Start-menu entry, so
/// the first launch can offer to drop a desktop shortcut. Creates the .lnk via the
/// WScript.Shell COM object (late-bound by ProgID, no extra NuGet dependency) --
/// the standard way to create shortcuts from .NET without shelling out.
/// </summary>
internal static class DesktopShortcut
{
    public static bool TryCreate()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, "VibeCast.lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            var shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.Description = "VibeCast";
            shortcut.Save();
            return true;
        }
        catch (Exception)
        {
            // Best-effort -- a failed shortcut creation isn't worth surfacing as an
            // error, the user can still launch VibeCast.exe directly.
            return false;
        }
    }
}
