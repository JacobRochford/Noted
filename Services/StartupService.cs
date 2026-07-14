using Microsoft.Win32;

namespace Noted.Services;

// handles Windows run-on-startup registry entry
public sealed class StartupService : IStartupService {
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Noted";

    public bool IsRunOnStartupEnabled {
        get {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var stored = key?.GetValue(AppName) as string;
            if (string.IsNullOrEmpty(stored)) return false;
            // strip quotes, compare path
            return string.Equals(stored.Trim('"'), ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetRunOnStartup(bool enabled) {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enabled)
            key.SetValue(AppName, $"\"{ExecutablePath}\""); // always quote path
        else
            key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    // get real exe path for Run key (works on .NET 6+ and fallback)
    private static string ExecutablePath =>
        Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
}
