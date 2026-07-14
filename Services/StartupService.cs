using System.IO;
using System.Security;
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

    public (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled) {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return BuildFailureResult("The Windows startup registry key could not be opened for writing.");

            if (enabled)
                key.SetValue(AppName, $"\"{ExecutablePath}\""); // always quote path
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        } catch (Exception ex) when (IsExpectedRegistryException(ex)) {
            return BuildFailureResult($"Windows could not update the startup registration: {ex.Message}");
        }

        try {
            var actualEnabled = IsRunOnStartupEnabled;
            if (actualEnabled == enabled)
                return (true, actualEnabled, null);

            return (
                false,
                actualEnabled,
                "Windows reported a different startup registration state than the requested setting.");
        } catch (Exception ex) when (IsExpectedRegistryException(ex)) {
            return (
                false,
                null,
                $"The startup registration was updated, but its current state could not be verified: {ex.Message}");
        }
    }

    private (bool Success, bool? ActualEnabled, string? Error) BuildFailureResult(string error) {
        try {
            return (false, IsRunOnStartupEnabled, error);
        } catch (Exception ex) when (IsExpectedRegistryException(ex)) {
            return (
                false,
                null,
                $"{error} The current startup registration state could not be verified: {ex.Message}");
        }
    }

    private static bool IsExpectedRegistryException(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException;

    // get real exe path for Run key (works on .NET 6+ and fallback)
    private static string ExecutablePath =>
        Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
}
