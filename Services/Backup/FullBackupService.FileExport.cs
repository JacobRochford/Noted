using System.IO;
using System.Security.Cryptography;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed partial class FullBackupService
{
    internal BackupExportResult ExportBeforeRestoreBackupFile(
        BeforeRestoreBackup backup, BackupFileSummary file, string destinationPath)
    {
        string? tempPath = null;
        try
        {
            var backupPath = GetBeforeRestoreBackupPath(backup);
            var verified = _archiveReader.ReadBackup(backupPath, FullBackupType.BeforeRestore);
            if (verified.Status != BackupReadStatus.Valid)
                throw new InvalidDataException("The snapshot could not be verified. Close this window and check it again.");

            var (manifest, entry) = _archiveReader.ReadBackupSelection(backupPath, FullBackupType.BeforeRestore,
                backup.Preview.BackupId, file, backup.Preview.VerificationToken);
            var exportPath = ValidateFileExportDestination(destinationPath, manifest.NotesRoot);
            var candidate = Path.Combine(Path.GetDirectoryName(exportPath)!, $".noted-export-{Guid.NewGuid():N}.tmp");
            var sourcePath = GetContainedPath(Path.Combine(backupPath, FilesDirectoryName), entry.LogicalPath);
            FileSystemLinkValidator.ValidatePath(sourcePath);
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            FileSystemLinkValidator.ValidatePath(candidate);
            using (var destination = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                // only clean up a temporary file this operation actually created.
                tempPath = candidate;
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            PublishNewBackupFile(tempPath, exportPath, entry, () =>
            {
                _ = GetBeforeRestoreBackupPath(backup);
                _archiveReader.ReadBackupSelection(backupPath, FullBackupType.BeforeRestore,
                    backup.Preview.BackupId, file, backup.Preview.VerificationToken);
                if (!PathsEqual(exportPath, ValidateFileExportDestination(destinationPath, manifest.NotesRoot)))
                    throw new IOException("The export folder changed. Choose the destination again.");
            });
            tempPath = null;
            return new BackupExportResult(true, exportPath, $"Exported the verified file to '{exportPath}'.");
        }
        catch (Exception ex) when (IsExpectedBackupException(ex) || ex is ArgumentException or NotSupportedException)
        {
            ExceptionDiagnostics.Record(ex);
            var warning = tempPath is null ? null : CleanupFileExport(tempPath);
            return new BackupExportResult(false, null,
                ex is InvalidDataException ? ex.Message : "The file could not be exported. Choose a new filename in an accessible folder outside Noted's data folders.",
                warning);
        }
    }

    private string ValidateFileExportDestination(string destinationPath, string snapshotNotesRoot)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
            throw new InvalidDataException("Choose an existing export folder and a new filename.");
        var name = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException("Choose a valid filename for the exported file.");

        var directory = FileSystemLinkValidator.ResolveDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        FileSystemLinkValidator.ValidateDirectory(directory);
        if (!Directory.Exists(directory))
            throw new InvalidDataException("Choose an existing destination folder.");

        foreach (var root in new[] { _appDataDirectory, _backupDirectory, _notesDirectory, snapshotNotesRoot })
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidDataException("The snapshot's protected folders could not be verified.");
            var resolvedRoot = FileSystemLinkValidator.ResolveDirectory(root);
            if (PathsEqual(directory, resolvedRoot) || IsPathWithin(directory, resolvedRoot))
                throw new InvalidDataException("Choose an export folder outside Noted's application data, backup storage, and current and original notes folders.");
        }

        var exportPath = Path.Combine(directory, name);
        FileSystemLinkValidator.ValidatePath(exportPath);
        if (FileSystemLinkValidator.GetAttributes(exportPath) is not null)
            throw new InvalidDataException("The destination already exists. Choose a new filename; existing files are never overwritten.");
        return exportPath;
    }

    internal static void PublishNewBackupFile(
        string tempPath, string exportPath, FullBackupEntry entry, Action recheck)
    {
        recheck();
        FileSystemLinkValidator.ValidatePath(tempPath);
        // keep the verified file open without write sharing until its no-overwrite move completes.
        using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length != entry.Length ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The exported bytes did not match the selected backup file. Nothing was published.");
        FileSystemLinkValidator.ValidatePath(tempPath);
        FileSystemLinkValidator.ValidatePath(exportPath);
        File.Move(tempPath, exportPath, overwrite: false);
    }

    internal static string? CleanupFileExport(string tempPath)
    {
        try
        {
            FileSystemLinkValidator.ValidatePath(tempPath);
            File.Delete(tempPath);
            return null;
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return $"The temporary export could not be removed: '{tempPath}'. No existing destination file was replaced.";
        }
    }
}
