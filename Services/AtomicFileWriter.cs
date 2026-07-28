using System.IO;
using System.Security;
using System.Text;

namespace Noted.Services;

internal static class AtomicFileWriter
{
    private static readonly Encoding Utf8WithoutBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static void WriteAllText(string destinationPath, string content)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, content, Utf8WithoutBom);

            if (File.Exists(destinationPath))
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }
}
