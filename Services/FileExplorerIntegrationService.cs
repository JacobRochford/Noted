using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Noted.Services;

internal static class FileExplorerIntegrationService
{
    private const string ApplicationKeyPath = @"Software\Classes\Applications\Noted.exe";
    private const string ExplorerVerbName = "Noted";
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);

    internal static bool IsEnabled()
    {
        try
        {
            var expectedCommand = BuildOpenCommand();
            using var commandKey = Registry.CurrentUser.OpenSubKey($@"{ApplicationKeyPath}\shell\open\command");
            if (!string.Equals(
                    commandKey?.GetValue(null) as string,
                    expectedCommand,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var extension in NoteFileExtensions.Supported)
            {
                using var extensionCommandKey = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\SystemFileAssociations\{extension}\shell\{ExplorerVerbName}\command");
                if (!string.Equals(
                        extensionCommandKey?.GetValue(null) as string,
                        expectedCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is
                   IOException or
                   InvalidOperationException or
                   SecurityException or
                   UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (enabled)
            Register();
        else
            Unregister();
    }

    private static void Register()
    {
        var executablePath = GetExecutablePath();
        using (var appKey = Registry.CurrentUser.CreateSubKey(ApplicationKeyPath))
            appKey.SetValue("FriendlyAppName", "Noted");

        using (var iconKey = Registry.CurrentUser.CreateSubKey($@"{ApplicationKeyPath}\DefaultIcon"))
            iconKey.SetValue(null, $"\"{executablePath}\",0");

        using (var supportedTypes = Registry.CurrentUser.CreateSubKey($@"{ApplicationKeyPath}\SupportedTypes"))
        {
            foreach (var extension in NoteFileExtensions.Supported)
                supportedTypes.SetValue(extension, string.Empty);
        }

        using (var commandKey = Registry.CurrentUser.CreateSubKey($@"{ApplicationKeyPath}\shell\open\command"))
            commandKey.SetValue(null, BuildOpenCommand());

        foreach (var extension in NoteFileExtensions.Supported)
        {
            using var verbKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\{ExplorerVerbName}");
            verbKey.SetValue(null, "Open with Noted");
            verbKey.SetValue("Icon", $"\"{executablePath}\",0");
            using var commandKey = verbKey.CreateSubKey("command");
            commandKey.SetValue(null, BuildOpenCommand());
        }

        NotifyExplorer();
    }

    private static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ApplicationKeyPath, throwOnMissingSubKey: false);
        foreach (var extension in NoteFileExtensions.Supported)
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\SystemFileAssociations\{extension}\shell\{ExplorerVerbName}",
                throwOnMissingSubKey: false);
        }

        NotifyExplorer();
    }

    private static string BuildOpenCommand() => $"\"{GetExecutablePath()}\" \"%1\"";

    private static string GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Noted could not determine its executable path.");
        return Path.GetFullPath(path);
    }

    private static void NotifyExplorer()
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
