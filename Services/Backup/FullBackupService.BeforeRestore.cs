using System.IO;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed partial class FullBackupService
{
    private const int BeforeRestoreBackupLimit = 3;

    internal BackupRestorePreparationResult PrepareBackupRestore(
        string? archivePath, Guid? expectedBackupId, string? verificationToken)
    {
        try
        {
            FullBackupManifest incoming;
            if (archivePath is null)
            {
                var backup = _archiveReader.ReadBackup(GetBackupPath(FullBackupType.User), FullBackupType.User);
                if (backup.Status != BackupReadStatus.Valid)
                    throw new InvalidDataException("The user backup could not be verified before protecting restore destinations.");
                incoming = backup.Manifest!;
            }
            else
            {
                var archive = _archiveReader.ReadBackupArchive(archivePath);
                if (archive.Manifest.BackupId != expectedBackupId ||
                    !string.Equals(archive.ManifestHash, verificationToken, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The imported backup changed after it was previewed.");
                incoming = CreateImportedManifest(archive.Manifest, archive.Manifest.BackupId,
                    _archiveReader.InspectImportedFiles(archive));
            }

            var protection = CreateBeforeRestoreBackup(incoming);
            if (protection.Status is not (FullBackupStatus.Created or FullBackupStatus.Skipped))
                return new BackupRestorePreparationResult(protection, null);
            var scheduled = archivePath is null
                ? ScheduleUserBackupRestore(incoming)
                : ScheduleBackupImportRestore(archivePath, expectedBackupId!.Value, verificationToken!);
            return new BackupRestorePreparationResult(protection, scheduled);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new BackupRestorePreparationResult(new FullBackupResult(
                FullBackupStatus.Failed, FullBackupType.BeforeRestore, null,
                "Restore destinations could not be verified and protected. Nothing was scheduled."), null);
        }
    }

    internal FullBackupResult CreateBeforeRestoreBackup(FullBackupManifest? incoming = null)
    {
        try
        {
            var interruptedWork = FindInterruptedWork();
            if (interruptedWork.Count > 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.BeforeRestore,
                    null,
                    "Current data could not be protected before restoration because unfinished backup work needs review: " +
                    string.Join(", ", interruptedWork.Select(Path.GetFileName)));
            }

            var notesRoot = incoming?.NotesRoot ?? _notesDirectory;
            // a restore targets one notes root; documents in a different current root are not overwritten.
            var currentFiles = _contentCatalog.Collect(out _, out _,
                includeNotes: PathsEqual(notesRoot, _notesDirectory)).ToList();
            if (incoming is not null)
            {
                var capturedPaths = currentFiles.ToDictionary(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in incoming.Entries)
                {
                    var logicalPath = MiniPadStorage.NormalizeBackupPath(NormalizeLogicalPath(entry.LogicalPath));
                    var destination = _backupRestore.ValidateDestination(logicalPath, incoming);
                    if (capturedPaths.ContainsKey(logicalPath))
                        continue;
                    var source = _contentCatalog.CaptureRestoreDestination(logicalPath, destination, notesRoot);
                    if (source is not null)
                    {
                        capturedPaths.Add(logicalPath, source);
                        currentFiles.Add(source);
                    }
                }
            }
            if (currentFiles.Count == 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Skipped,
                    FullBackupType.BeforeRestore,
                    null,
                    "There is no current Noted data to protect before restoration.");
            }

            var build = _archiveWriter.BuildVerifiedBackup(FullBackupType.BeforeRestore, currentFiles, notesRoot);
            var backupPath = Path.Combine(
                _backupDirectory,
                $"{BeforeRestoreDirectoryPrefix}{_clock.GetUtcNow().UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            FileSystemLinkValidator.ValidateTree(build.Path);
            FileSystemLinkValidator.ValidateDirectory(backupPath);
            Directory.Move(build.Path, backupPath);
            _archiveReader.VerifyBackupFolder(backupPath, FullBackupType.BeforeRestore);

            string? warning = null;
            try
            {
                TrimBeforeRestoreBackups();
            }
            catch (Exception ex) when (IsExpectedBackupException(ex))
            {
                ExceptionDiagnostics.Record(ex);
                warning = "The current data is protected, but an older before-restore backup could not be removed.";
            }

            return new FullBackupResult(
                FullBackupStatus.Created,
                FullBackupType.BeforeRestore,
                backupPath,
                $"Protected {currentFiles.Count} current files before restoration.",
                warning);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.BeforeRestore,
                null,
                "Current data could not be protected before restoration.");
        }
    }

    private void TrimBeforeRestoreBackups()
    {
        FileSystemLinkValidator.ValidateDirectory(_backupDirectory);
        var backups = Directory
            .EnumerateDirectories(
                _backupDirectory,
                BeforeRestoreDirectoryPrefix + "*",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in backups.Skip(BeforeRestoreBackupLimit))
            DeleteBeforeRestoreBackup(path);
    }

    private void DeleteBeforeRestoreBackup(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.EndsInDirectorySeparator(_backupDirectory)
            ? _backupDirectory
            : _backupDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(BeforeRestoreDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The before-restore backup is outside Noted's backup storage.");
        }

        FileSystemLinkValidator.ValidateTree(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }
}
