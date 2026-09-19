using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed class BackupArchiveWriter
{
    private readonly string _appDataDirectory;
    private string _notesDirectory;
    private readonly string _backupDirectory;
    private readonly TimeProvider _clock;
    private readonly BackupArchiveReader _archiveReader;

    internal BackupArchiveWriter(
        string appDataDirectory,
        string notesDirectory,
        string backupDirectory,
        TimeProvider clock,
        BackupArchiveReader archiveReader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(archiveReader);

        _appDataDirectory = Path.GetFullPath(appDataDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _clock = clock;
        _archiveReader = archiveReader;
    }

    internal void UpdateNotesDirectory(string notesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
    }

    internal BackupBuildResult BuildVerifiedBackup(
        FullBackupType backupType,
        IReadOnlyList<BackupSourceEntry> backupFiles)
    {
        Directory.CreateDirectory(_backupDirectory);
        var buildPath = Path.Combine(_backupDirectory, $".building-{Guid.NewGuid():N}");
        Directory.CreateDirectory(buildPath);
        var filesDirectory = Path.Combine(buildPath, FilesDirectoryName);
        Directory.CreateDirectory(filesDirectory);

        foreach (var source in backupFiles)
        {
            var destinationPath = GetContainedPath(filesDirectory, source.LogicalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            CopyAndVerify(source, destinationPath);
        }

        var manifest = new FullBackupManifest(
            BackupSchemaVersion,
            Guid.NewGuid(),
            backupType,
            _clock.GetUtcNow().UtcDateTime,
            _appDataDirectory,
            _notesDirectory,
            backupFiles.Select(source => source.ToManifestEntry()).ToList());
        JsonFileStore.Write(
            Path.Combine(buildPath, ManifestFileName),
            manifest,
            options: BackupFormat.JsonOptions);

        var verification = _archiveReader.ReadBackup(buildPath, backupType);
        if (verification.Status != BackupReadStatus.Valid)
        {
            throw new FileVerificationException(
                $"The new full backup was built but could not be verified: {verification.Error}");
        }

        return new BackupBuildResult(buildPath);
    }

    internal BackupReplaceResult ReplaceBackupFolder(
        string buildPath,
        FullBackupType backupType)
    {
        var backupPath = GetBackupPath(backupType);
        if (!Directory.Exists(backupPath))
        {
            Directory.Move(buildPath, backupPath);
            _archiveReader.VerifyBackupFolder(backupPath, backupType);
            return new BackupReplaceResult(backupPath, null);
        }

        var retiredPath = Path.Combine(
            _backupDirectory,
            $".replacing-{GetBackupFolderName(backupType)}-{Guid.NewGuid():N}");
        Directory.Move(backupPath, retiredPath);
        try
        {
            Directory.Move(buildPath, backupPath);
            _archiveReader.VerifyBackupFolder(backupPath, backupType);
        }
        catch
        {
            if (Directory.Exists(backupPath))
            {
                var failedPath = Path.Combine(
                    _backupDirectory,
                    $".failed-{GetBackupFolderName(backupType)}-{Guid.NewGuid():N}");
                Directory.Move(backupPath, failedPath);
            }
            if (Directory.Exists(retiredPath))
                Directory.Move(retiredPath, backupPath);
            throw;
        }

        if (backupType == FullBackupType.User)
            return PreservePreviousUserBackup(backupPath, retiredPath);

        try
        {
            DeleteBackupDirectory(retiredPath);
            return new BackupReplaceResult(backupPath, null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new BackupReplaceResult(
                backupPath,
                $"The new recent backup is verified, but the retired recent backup could not be removed from '{retiredPath}'.");
        }
    }

    private BackupReplaceResult PreservePreviousUserBackup(
        string userBackupPath,
        string retiredUserBackupPath)
    {
        var previousPath = Path.Combine(_backupDirectory, "previous");
        string? olderPreviousPath = null;
        try
        {
            if (Directory.Exists(previousPath))
            {
                olderPreviousPath = Path.Combine(
                    _backupDirectory,
                    $".replacing-previous-{Guid.NewGuid():N}");
                Directory.Move(previousPath, olderPreviousPath);
            }

            Directory.Move(retiredUserBackupPath, previousPath);
            _archiveReader.VerifyBackupFolder(previousPath, FullBackupType.User);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            if (!Directory.Exists(previousPath) &&
                olderPreviousPath is not null &&
                Directory.Exists(olderPreviousPath))
            {
                Directory.Move(olderPreviousPath, previousPath);
                olderPreviousPath = null;
            }

            return new BackupReplaceResult(
                userBackupPath,
                "The updated backup is verified, but the previous backup could not be filed normally. Its data was left unchanged for review.");
        }

        if (olderPreviousPath is null)
            return new BackupReplaceResult(userBackupPath, null);

        try
        {
            DeleteBackupDirectory(olderPreviousPath);
            return new BackupReplaceResult(userBackupPath, null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new BackupReplaceResult(
                userBackupPath,
                $"The current and previous backups are verified, but an older backup folder could not be removed from '{olderPreviousPath}'.");
        }
    }

    private static void CopyAndVerify(BackupSourceEntry source, string destinationPath)
    {
        using (var sourceStream = new FileStream(
                   source.SourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        using (var destinationStream = new FileStream(
                   destinationPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 64 * 1024,
                   FileOptions.WriteThrough))
        {
            sourceStream.CopyTo(destinationStream);
            destinationStream.Flush(flushToDisk: true);
        }

        var copiedBytes = BackupArchiveReader.ReadFileBytes(destinationPath);
        var copiedHash = Convert.ToHexString(SHA256.HashData(copiedBytes));
        if (copiedBytes.LongLength != source.Length ||
            !string.Equals(copiedHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileVerificationException(
                $"'{source.SourcePath}' changed or could not be copied exactly while the full backup was being created.");
        }
    }

    private string GetBackupPath(FullBackupType backupType) =>
        Path.Combine(_backupDirectory, GetBackupFolderName(backupType));

    private static string GetBackupFolderName(FullBackupType backupType) =>
        backupType switch
        {
            FullBackupType.User => "protected",
            FullBackupType.Recent => "latest",
            _ => throw new ArgumentOutOfRangeException(nameof(backupType))
        };

    private void DeleteBackupDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.EndsInDirectorySeparator(_backupDirectory)
            ? _backupDirectory
            : _backupDirectory + Path.DirectorySeparatorChar;
        var directoryName = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            (!directoryName.StartsWith(".replacing-", StringComparison.OrdinalIgnoreCase) &&
             !directoryName.StartsWith(".failed-", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("The retired backup path is outside the expected backup directory.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private static bool IsExpectedBackupException(Exception exception) =>
        FileSystemErrors.IsExpected(exception) || exception is JsonException or InvalidDataException;
}

internal sealed record BackupBuildResult(string Path);

internal sealed record BackupReplaceResult(string Path, string? Warning);
