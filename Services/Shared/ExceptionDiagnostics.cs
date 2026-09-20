using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Noted.Services;

internal static class ExceptionDiagnostics
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object s_lock = new();

    internal static void Record(Exception exception, [CallerMemberName] string context = "")
    {
        Write(exception, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Noted", "logs", "exceptions.log"), context);
    }

    internal static void Write(Exception exception, string logPath, string context)
    {
        // Don't let a logging failure hide the original exception
        try
        {
            var entry = $"{DateTimeOffset.UtcNow:O} [{context}]{Environment.NewLine}{exception}{Environment.NewLine}";
            Trace.WriteLine(entry);
            lock (s_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaximumLogBytes)
                    File.Move(logPath, logPath + ".previous", overwrite: true);
                File.AppendAllText(logPath, entry, Encoding.UTF8);
            }
        }
        catch (Exception loggingException)
        {
            Debug.WriteLine(exception);
            Debug.WriteLine(loggingException);
        }
    }
}
