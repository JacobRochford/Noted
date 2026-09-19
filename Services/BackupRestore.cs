using System.IO;
using System.Text.Json;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed class BackupRestore
{
    private const string RestoreRequestFileName = "restore-request.json";

    private readonly string _backupDirectory;
    private readonly BackupArchiveReader _archiveReader;
    private readonly Func<string, FullBackupManifest, string> _getRestoreDestination;
    private readonly Action<string> _deleteImportDirectory;

    internal BackupRestore(
        string backupDirectory,
        BackupArchiveReader archiveReader,
        Func<string, FullBackupManifest, string> getRestoreDestination,
        Action<string> deleteImportDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentNullException.ThrowIfNull(archiveReader);
        ArgumentNullException.ThrowIfNull(getRestoreDestination);
        ArgumentNullException.ThrowIfNull(deleteImportDirectory);

        _backupDirectory = Path.GetFullPath(backupDirectory);
        _archiveReader = archiveReader;
        _getRestoreDestination = getRestoreDestination;
        _deleteImportDirectory = deleteImportDirectory;
    }

    internal FullBackupResult ApplyPendingUserBackupRestore()
    {
        var requestPath = Path.Combine(_backupDirectory, RestoreRequestFileName);
        var requestResult = JsonFileStore.Read<FullBackupRestoreRequest>(
            requestPath,
            BackupFormat.JsonOptions);
        if (requestResult.Status == JsonFileReadStatus.Missing)
        {
            return new FullBackupResult(
                FullBackupStatus.Skipped,
                null,
                null,
                "No backup restore is pending.");
        }

        try
        {
            if (!requestResult.Success || requestResult.Value is null)
            {
                throw new FileVerificationException(
                    "The pending backup restore request is invalid.",
                    requestResult.Error);
            }

            var request = requestResult.Value;
            if (request.SchemaVersion != BackupSchemaVersion ||
                request.BackupId == Guid.Empty)
            {
                throw new FileVerificationException(
                    "The pending backup restore request is not supported.");
            }

            if (request.ImportId == Guid.Empty)
            {
                throw new FileVerificationException(
                    "The pending backup import ID is invalid.");
            }

            var restorePath = request.ImportId.HasValue
                ? Path.Combine(_backupDirectory, $"pending-import-{request.ImportId.Value:N}")
                : Path.Combine(_backupDirectory, "protected");
            var restoreBackup = _archiveReader.ReadBackup(restorePath, FullBackupType.User);
            if (restoreBackup.Status != BackupReadStatus.Valid)
            {
                throw new FileVerificationException(
                    $"The backup cannot be restored: {restoreBackup.Error ?? "the backup is missing"}.");
            }

            var manifest = restoreBackup.Manifest!;
            if (manifest.BackupId != request.BackupId)
            {
                throw new FileVerificationException(
                    "The backup changed after the restore was requested. Nothing was restored.");
            }

            var filesDirectory = Path.Combine(restorePath, FilesDirectoryName);
            foreach (var entry in manifest.Entries
                         .OrderBy(entry => entry.LogicalPath, StringComparer.OrdinalIgnoreCase))
            {
                var sourcePath = GetContainedPath(filesDirectory, entry.LogicalPath);
                var destinationPath = _getRestoreDestination(entry.LogicalPath, manifest);
                var bytes = BackupArchiveReader.ReadFileBytes(sourcePath);
                BackupArchiveReader.VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
                FileWriter.WriteAllBytes(destinationPath, bytes);
                var restoredBytes = BackupArchiveReader.ReadFileBytes(destinationPath);
                BackupArchiveReader.VerifyFileBytes(restoredBytes, entry.Length, entry.Sha256, entry.LogicalPath);
            }

            FileWriter.DeleteIfExists(requestPath);
            string? warning = null;
            if (request.ImportId.HasValue)
            {
                try
                {
                    var restoredImportPath = Path.Combine(
                        _backupDirectory,
                        $".restored-import-{request.ImportId.Value:N}");
                    Directory.Move(restorePath, restoredImportPath);
                    _deleteImportDirectory(restoredImportPath);
                }
                catch (Exception ex) when (IsExpectedBackupException(ex))
                {
                    ExceptionDiagnostics.Record(ex);
                    warning = "The imported backup was restored, but its temporary local copy could not be removed.";
                }
            }

            return new FullBackupResult(
                FullBackupStatus.Restored,
                FullBackupType.User,
                restorePath,
                request.ImportId.HasValue
                    ? $"Restored {manifest.Entries.Count} files from the imported backup. Your user backup was not changed."
                    : $"Restored {manifest.Entries.Count} files from your verified backup. The backup was not changed.",
                warning);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                "The backup restore did not finish. The backup and restore request were left unchanged so Noted can retry.");
        }
    }

    private static bool IsExpectedBackupException(Exception exception) =>
        FileSystemErrors.IsExpected(exception) || exception is JsonException or InvalidDataException;
}
