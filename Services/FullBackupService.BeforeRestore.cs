using System.IO;

namespace Noted.Services;

internal sealed partial class FullBackupService
{
    private const int BeforeRestoreBackupLimit = 3;

    internal FullBackupResult CreateBeforeRestoreBackup()
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

            var currentFiles = CollectBackupFiles();
            if (currentFiles.Count == 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Skipped,
                    FullBackupType.BeforeRestore,
                    null,
                    "There is no current Noted data to protect before restoration.");
            }

            var build = BuildVerifiedBackup(FullBackupType.BeforeRestore, currentFiles);
            var backupPath = Path.Combine(
                _backupDirectory,
                $"before-restore-{_clock.GetUtcNow().UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            Directory.Move(build.Path, backupPath);
            VerifyBackupFolder(backupPath, FullBackupType.BeforeRestore);

            string? warning = null;
            try
            {
                TrimBeforeRestoreBackups();
            }
            catch (Exception ex) when (IsExpectedBackupException(ex))
            {
                System.Diagnostics.Debug.WriteLine(ex);
                warning = $"The current data is protected, but an older before-restore backup could not be removed: {ex.Message}";
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
            System.Diagnostics.Debug.WriteLine(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.BeforeRestore,
                null,
                $"Current data could not be protected before restoration: {ex.Message}");
        }
    }

    private void TrimBeforeRestoreBackups()
    {
        var backups = Directory
            .EnumerateDirectories(
                _backupDirectory,
                "before-restore-*",
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
            !Path.GetFileName(fullPath).StartsWith("before-restore-", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The before-restore backup is outside Noted's backup storage.");
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
