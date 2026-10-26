using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed class BackupRestore
{

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
        try
        {
            FileSystemLinkValidator.ValidateDirectory(_backupDirectory);
            FileSystemLinkValidator.ValidatePath(requestPath);
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
                ? Path.Combine(_backupDirectory, $"{PendingImportDirectoryPrefix}{request.ImportId.Value:N}")
                : Path.Combine(_backupDirectory, UserDirectoryName);
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

            // check all destinations before writing anything; keep the request if a check fails.
            foreach (var entry in manifest.Entries)
                ValidateDestination(entry.LogicalPath, manifest);

            var filesDirectory = Path.Combine(restorePath, FilesDirectoryName);
            var retainedDrafts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (BackupContentDefinitions.Find(NormalizeLogicalPath(entry.LogicalPath))?.Content != FixedBackupContent.JotCapture)
                    continue;
                var bytes = BackupArchiveReader.ReadFileBytes(GetContainedPath(filesDirectory, entry.LogicalPath));
                BackupArchiveReader.VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
                if (KeepLocalQuickJotDraft(ValidateDestination(entry.LogicalPath, manifest), bytes))
                    retainedDrafts.Add(entry.LogicalPath);
            }
            var restoredCount = 0;
            foreach (var entry in manifest.Entries
                         .OrderBy(entry => entry.LogicalPath, StringComparer.OrdinalIgnoreCase))
            {
                if (retainedDrafts.Contains(entry.LogicalPath))
                    continue;
                var sourcePath = GetContainedPath(filesDirectory, entry.LogicalPath);
                var destinationPath = ValidateDestination(entry.LogicalPath, manifest);
                if (BackupContentDefinitions.Find(NormalizeLogicalPath(entry.LogicalPath))?.Content != FixedBackupContent.JotCapture)
                {
                    FileWriter.WriteFile(destinationPath, null, temporaryPath =>
                    {
                        FileSystemLinkValidator.ValidatePath(sourcePath);
                        FileSystemLinkValidator.ValidatePath(temporaryPath);
                        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            source.CopyTo(output);
                            output.Flush(flushToDisk: true);
                        }
                        BackupArchiveReader.VerifyFile(temporaryPath, entry);
                        if (!string.Equals(destinationPath, ValidateDestination(entry.LogicalPath, manifest), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("The restore destination changed during preparation.");
                    });
                    BackupArchiveReader.VerifyFile(destinationPath, entry);
                    restoredCount++;
                    continue;
                }
                var bytes = BackupArchiveReader.ReadFileBytes(sourcePath);
                BackupArchiveReader.VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
                destinationPath = ValidateDestination(entry.LogicalPath, manifest);
                var isQuickJot = BackupContentDefinitions.Find(NormalizeLogicalPath(entry.LogicalPath))?.Content == FixedBackupContent.JotCapture;
                if (isQuickJot && KeepLocalQuickJotDraft(destinationPath, bytes))
                {
                    retainedDrafts.Add(entry.LogicalPath);
                    continue;
                }
                var clearedQuickJot = isQuickJot && string.IsNullOrEmpty(
                    JotCaptureDraftService.ValidateBackupDraft(bytes)?.Text);
                if (clearedQuickJot)
                {
                    var service = new JotCaptureDraftService(Path.GetDirectoryName(destinationPath)!);
                    service.Load(out _);
                    service.Save(JotCaptureDraftService.ValidateBackupDraft(bytes) ?? new());
                }
                if (isQuickJot)
                    FileSystemLinkValidator.ValidatePath($"{destinationPath}.bak");
                var writeResult = FileWriter.WriteAllBytes(destinationPath, bytes,
                    isQuickJot && !clearedQuickJot ? $"{destinationPath}.bak" : null);
                if (writeResult.Warning is not null)
                    throw new IOException(writeResult.Warning);
                var restoredBytes = BackupArchiveReader.ReadFileBytes(destinationPath);
                BackupArchiveReader.VerifyFileBytes(restoredBytes, entry.Length, entry.Sha256, entry.LogicalPath);
                restoredCount++;
            }

            FileSystemLinkValidator.ValidatePath(requestPath);
            FileWriter.DeleteIfExists(requestPath);
            string? warning = retainedDrafts.Count > 0
                ? $"Your unfinished local Quick Jot draft was kept instead of replacing it with the backup's draft. Open Quick Jot to review it. Any different nonempty incoming draft was preserved at '{Path.Combine(_backupDirectory, PersistencePaths.JotCaptureConflictsDirectory)}'."
                : null;
            if (request.ImportId.HasValue)
            {
                try
                {
                    var restoredImportPath = Path.Combine(
                        _backupDirectory,
                        $"{RestoredImportDirectoryPrefix}{request.ImportId.Value:N}");
                    FileSystemLinkValidator.ValidateTree(restorePath);
                    FileSystemLinkValidator.ValidateDirectory(restoredImportPath);
                    Directory.Move(restorePath, restoredImportPath);
                    _deleteImportDirectory(restoredImportPath);
                }
                catch (Exception ex) when (IsExpectedBackupException(ex))
                {
                    ExceptionDiagnostics.Record(ex);
                    warning = string.Join(" ", new[] { warning,
                        "The imported backup was restored, but its temporary local copy could not be removed." }.Where(message => message is not null));
                }
            }

            return new FullBackupResult(
                FullBackupStatus.Restored,
                FullBackupType.User,
                restorePath,
                request.ImportId.HasValue
                    ? $"Restored {restoredCount} files from the imported backup. Your user backup was not changed."
                    : $"Restored {restoredCount} files from your verified backup. The backup was not changed.",
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

    private bool KeepLocalQuickJotDraft(string destinationPath, byte[] bytes)
    {
        var incoming = JotCaptureDraftService.ValidateBackupDraft(bytes);
        var service = new JotCaptureDraftService(Path.GetDirectoryName(destinationPath)!);
        var current = service.Load(out var recoveryWarning);
        if (service.WritesBlocked)
            throw new IOException("The local Quick Jot draft could not be recovered safely.");
        if (string.IsNullOrEmpty(current.Text) || current == incoming)
            return false;

        // keep recovered text at the primary path before other restore writes begin.
        if (recoveryWarning is not null)
        {
            var warning = service.Save(current);
            if (warning is not null)
                throw new IOException(warning);
        }
        if (incoming is not null && incoming.Text.Length > 0)
        {
            var conflictPath = Path.Combine(_backupDirectory, PersistencePaths.JotCaptureConflictsDirectory,
                $"{incoming.Id:N}-{Convert.ToHexString(SHA256.HashData(bytes))}.json");
            FileSystemLinkValidator.ValidatePath(conflictPath);
            if (!File.Exists(conflictPath))
                FileWriter.WriteAllBytes(conflictPath, bytes);
            var preserved = BackupArchiveReader.ReadFileBytes(conflictPath);
            if (!preserved.AsSpan().SequenceEqual(bytes))
                throw new FileVerificationException("The conflicting Quick Jot draft could not be preserved.");
            JotCaptureDraftService.ValidateBackupDraft(preserved);
        }
        return true;
    }

    internal string ValidateDestination(string logicalPath, FullBackupManifest manifest)
    {
        var destination = _getRestoreDestination(logicalPath, manifest);
        if (BackupFormat.NormalizeLogicalPath(logicalPath).StartsWith(PersistencePaths.NotesBackupPrefix, StringComparison.OrdinalIgnoreCase))
            return FileSystemLinkValidator.ValidateWithinRoot(manifest.NotesRoot, destination);
        FileSystemLinkValidator.ValidatePath(destination);
        return destination;
    }

    private static bool IsExpectedBackupException(Exception exception) =>
        FileSystemErrors.IsExpected(exception) || exception is JsonException or InvalidDataException;
}
