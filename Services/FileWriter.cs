using System.IO;
using System.Security;
using System.Text;

namespace Noted.Services;

internal readonly record struct FileWriteResult(
    bool FileUpdated,
    bool BackupUpdated,
    string? PreservedBackupPath,
    string? Warning);

internal static class FileWriter
{
    private static readonly Encoding Utf8WithoutBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static FileWriteResult WriteAllText(
        string destinationPath,
        string content,
        string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);

        return WriteFile(
            destinationPath,
            backupPath,
            temporaryPath => WriteTemporaryTextFile(temporaryPath, content));
    }

    internal static FileWriteResult WriteAllBytes(
        string destinationPath,
        byte[] content,
        string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(content);

        return WriteFile(
            destinationPath,
            backupPath,
            temporaryPath => WriteTemporaryByteFile(temporaryPath, content));
    }

    private static FileWriteResult WriteFile(
        string destinationPath,
        string? backupPath,
        Action<string> writeTemporaryFile)
    {
        ArgumentNullException.ThrowIfNull(writeTemporaryFile);

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
            writeTemporaryFile(temporaryPath);

            if (File.Exists(fullDestinationPath))
            {
                if (fullBackupPath is null)
                {
                    File.Replace(temporaryPath, fullDestinationPath, null);
                    return new FileWriteResult(true, false, null, null);
                }

                var rollbackPath = CreateRollbackPath(fullBackupPath);
                File.Replace(temporaryPath, fullDestinationPath, rollbackPath);
                return UpdateBackupFromRollback(rollbackPath, fullBackupPath);
            }

            File.Move(temporaryPath, fullDestinationPath);
            return new FileWriteResult(true, false, null, null);
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

    private static string CreateRollbackPath(string backupPath) =>
        $"{backupPath}.pending-{Guid.NewGuid():N}";

    private static FileWriteResult UpdateBackupFromRollback(
        string rollbackPath,
        string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
                File.Replace(rollbackPath, backupPath, null);
            else
                File.Move(rollbackPath, backupPath);

            return new FileWriteResult(true, true, null, null);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            var preservedPath = File.Exists(rollbackPath)
                ? rollbackPath
                : null;
            var preservationMessage = preservedPath is not null
                ? $" The previous file remains available at '{preservedPath}'."
                : " The previous file could not be confirmed at the rollback path.";
            var warning =
                $"The destination file was saved, but its rolling backup could not be updated: {ex.Message}" +
                preservationMessage;
            System.Diagnostics.Debug.WriteLine(warning);
            return new FileWriteResult(true, false, preservedPath, warning);
        }
    }

    private static void WriteTemporaryTextFile(string path, string content)
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

    private static void WriteTemporaryByteFile(string path, byte[] content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(content);
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

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
    }
}
