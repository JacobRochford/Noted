using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Noted.Tests.Services;

internal sealed class TestFileSystemLink : IDisposable
{
    private readonly string _path;
    private readonly bool _isDirectory;

    internal TestFileSystemLink(string path, string target, bool isDirectory = true)
    {
        _path = path;
        _isDirectory = isDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (isDirectory)
            {
                var start = new ProcessStartInfo("powershell.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
                    path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    Assert.Inconclusive("Junction creation is unavailable: " + error);
            }
            else
                File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Link creation is unavailable: " + ex.Message);
        }
    }

    public void Dispose()
    {
        // remove only the link before test cleanup recursively deletes the temporary folder.
        if (_isDirectory)
            Directory.Delete(_path, recursive: false);
        else
            File.Delete(_path);
    }
}
