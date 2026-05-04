using System.IO;
using System.Windows;
using Noted.Services;

namespace Noted;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        TryCreateDesktopShortcut();
        _ = UpdateService.CheckForUpdatesAsync();
    }

    private static void TryCreateDesktopShortcut()
    {
        try
        {
            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Noted.lnk");

            if (File.Exists(shortcutPath))
                return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = Environment.ProcessPath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Noted.exe");
            shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            shortcut.Save();
        }
        catch
        {
            // Best-effort; never crash the app over a missing shortcut.
        }
    }
}

