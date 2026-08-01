using System.IO;
using System.Security;
using System.Text;

namespace Noted.Services;

internal static class FileWriter
{
    private static readonly Encoding Utf8WithoutBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static void WriteAllText(
        string destinationPath,
        string content,
        string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
        var fullBackupPath = NormalizeBackupPath(backupPath, fullDestinationPath, directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            WriteTemporaryFile(temporaryPath, content);

            if (File.Exists(fullDestinationPath))
            {
                if (fullBackupPath is not null)
                    DeleteIfExists(fullBackupPath);

                File.Replace(temporaryPath, fullDestinationPath, fullBackupPath);
            }
            else
            {
                File.Move(temporaryPath, fullDestinationPath);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    internal static void DeleteIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    private static string? NormalizeBackupPath(
        string? backupPath,
        string destinationPath,
        string destinationDirectory)
    {
        if (backupPath is null)
            return null;

        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullBackupPath = Path.GetFullPath(backupPath);
        if (string.Equals(fullBackupPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The backup path must differ from the destination path.", nameof(backupPath));

        var backupDirectory = Path.GetDirectoryName(fullBackupPath);
        if (!string.Equals(backupDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The backup must be in the destination directory.", nameof(backupPath));

        return fullBackupPath;
    }

    private static void WriteTemporaryFile(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(
            stream,
            Utf8WithoutBom,
            bufferSize: 4096,
            leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
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
