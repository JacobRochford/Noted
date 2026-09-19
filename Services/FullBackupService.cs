using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Noted.Services.BackupContentCatalog;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal enum FullBackupStatus
{
    Created,
    RestoreScheduled,
    Restored,
    Skipped,
    Blocked,
    Failed
}

internal enum FullBackupType
{
    [JsonStringEnumMemberName("Protected")]
    User,

    [JsonStringEnumMemberName("Latest")]
    Recent,

    BeforeRestore
}

internal sealed record FullBackupResult(
    FullBackupStatus Status,
    FullBackupType? Type,
    string? BackupPath,
    string Message,
    string? Warning = null);

internal sealed record FullBackupInfo(
    bool Exists,
    bool IsValid,
    DateTime? LastUpdatedUtc,
    int FileCount,
    string? Error);

internal enum BackupFileComparison
{
    Unchanged,
    Changed,
    Missing,
    Unavailable
}

internal enum BackupContentFormat
{
    Text,
    Json,
    RichText
}

internal sealed record BackupFileSummary(
    string LogicalPath,
    string DisplayPath,
    string Category,
    long Length,
    string Sha256,
    int? ItemCount,
    long? ContentLength,
    BackupFileComparison Comparison,
    string? SourceLogicalPath = null);

internal sealed record FullBackupPreview(
    bool Exists,
    bool IsValid,
    Guid BackupId,
    DateTime? CreatedUtc,
    IReadOnlyList<BackupFileSummary> Files,
    string? Error,
    string? VerificationToken = null);

internal sealed record BackupFileContent(
    bool Success,
    bool NeedsRecheck,
    BackupContentFormat Format,
    byte[]? Data,
    string? Error);

internal sealed partial class FullBackupService
{
    private const string RestoreRequestFileName = "restore-request.json";
    private const long MaximumPreviewFileSize = 4 * 1024 * 1024;
    private readonly string _appDataDirectory;
    private string _notesDirectory;
    private readonly string _backupDirectory;
    private readonly BackupContentCatalog _contentCatalog;
    private readonly TimeProvider _clock;
    private bool _backupCreatedThisRun;

    internal FullBackupService(
        string appDataDirectory,
        string notesDirectory,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);

        _appDataDirectory = Path.GetFullPath(appDataDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
        _backupDirectory = Path.Combine(_appDataDirectory, "full-snapshots");
        _contentCatalog = new BackupContentCatalog(
            _appDataDirectory,
            _notesDirectory,
            _backupDirectory);
        _clock = clock ?? TimeProvider.System;
        TryCleanupFinishedImportFolders();
    }

    internal string BackupDirectory => _backupDirectory;

    internal bool UserBackupExists =>
        Directory.Exists(GetBackupPath(FullBackupType.User));

    internal FullBackupInfo GetUserBackupInfo()
    {
        var path = GetBackupPath(FullBackupType.User);
        var backup = ReadBackup(path, FullBackupType.User);
        return backup.Status switch
        {
            BackupReadStatus.Missing => new FullBackupInfo(false, false, null, 0, null),
            BackupReadStatus.Valid => new FullBackupInfo(
                true,
                true,
                backup.Manifest!.CreatedUtc,
                backup.Manifest.Entries.Count,
                null),
            _ => new FullBackupInfo(true, false, null, 0, backup.Error)
        };
    }

    internal FullBackupPreview GetUserBackupPreview()
    {
        var path = GetBackupPath(FullBackupType.User);
        var backup = ReadBackup(path, FullBackupType.User);
        if (backup.Status == BackupReadStatus.Missing)
            return new FullBackupPreview(false, false, Guid.Empty, null, [], null);
        if (backup.Status != BackupReadStatus.Valid)
            return new FullBackupPreview(true, false, Guid.Empty, null, [], backup.Error);

        var manifest = backup.Manifest!;
        var files = manifest.Entries
            .Select(entry => new BackupFileSummary(
                entry.LogicalPath,
                GetDisplayPath(entry.LogicalPath),
                GetFileCategory(entry.LogicalPath),
                entry.Length,
                entry.Sha256,
                entry.ItemCount,
                entry.ContentLength,
                CompareWithCurrentFile(entry, manifest)))
            .OrderBy(file => file.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FullBackupPreview(
            true,
            true,
            manifest.BackupId,
            manifest.CreatedUtc,
            files,
            null);
    }

    internal BackupFileContent ReadUserBackupFile(
        Guid backupId,
        BackupFileSummary file)
    {
        if (backupId == Guid.Empty || string.IsNullOrWhiteSpace(file.LogicalPath))
        {
            return new BackupFileContent(
                false,
                false,
                BackupContentFormat.Text,
                null,
                "Select a backup file to preview.");
        }

        var path = GetBackupPath(FullBackupType.User);
        var manifestResult = JsonFileStore.Read<FullBackupManifest>(
            Path.Combine(path, ManifestFileName),
            BackupFormat.JsonOptions);
        var manifest = manifestResult.Value;
        if (!manifestResult.Success ||
            manifest is null ||
            manifest.SchemaVersion != BackupSchemaVersion ||
            manifest.Type != FullBackupType.User ||
            manifest.BackupId != backupId ||
            manifest.Entries is null)
        {
            return new BackupFileContent(
                false,
                true,
                BackupContentFormat.Text,
                null,
                "The backup changed after this preview was opened. Close this window and check it again.");
        }

        var entry = manifest.Entries.FirstOrDefault(item =>
            string.Equals(
                NormalizeLogicalPath(item.LogicalPath),
                NormalizeLogicalPath(file.LogicalPath),
                StringComparison.OrdinalIgnoreCase));
        if (entry is null ||
            entry.Length != file.Length ||
            !string.Equals(entry.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new BackupFileContent(
                false,
                true,
                BackupContentFormat.Text,
                null,
                "The selected file changed after this preview was opened. Close this window and check it again.");
        }

        try
        {
            if (entry.Length > MaximumPreviewFileSize)
            {
                return new BackupFileContent(
                    false,
                    false,
                    BackupContentFormat.Text,
                    null,
                    "This file is verified but too large to display in the preview window.");
            }

            var filesDirectory = Path.Combine(path, FilesDirectoryName);
            var filePath = GetContainedPath(filesDirectory, entry.LogicalPath);
            var bytes = ReadFileBytes(filePath);
            VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
            return new BackupFileContent(
                true,
                false,
                GetContentFormat(entry.LogicalPath),
                bytes,
                null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new BackupFileContent(
                false,
                true,
                BackupContentFormat.Text,
                null,
                "The selected file could not be verified.");
        }
    }

    internal void UpdateNotesDirectory(string notesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
        _contentCatalog.UpdateNotesDirectory(_notesDirectory);
    }

    internal FullBackupResult CreateOrUpdateUserBackup() =>
        CreateBackup(FullBackupType.User, minimumAge: null);

    internal FullBackupResult CreateRecentBackup() =>
        CreateBackup(FullBackupType.Recent, minimumAge: null);

    internal FullBackupResult CreateRecentBackupIfDue(TimeSpan minimumAge)
    {
        if (minimumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumAge));

        return CreateBackup(FullBackupType.Recent, minimumAge);
    }

    internal FullBackupResult ScheduleUserBackupRestore()
    {
        try
        {
            var interruptedWork = FindInterruptedWork();
            if (interruptedWork.Count > 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.User,
                    null,
                    "The backup cannot be restored while unfinished backup work needs review: " +
                    string.Join(", ", interruptedWork.Select(Path.GetFileName)));
            }

            var userBackupPath = GetBackupPath(FullBackupType.User);
            var userBackup = ReadBackup(userBackupPath, FullBackupType.User);
            if (userBackup.Status != BackupReadStatus.Valid)
            {
                var explanation = userBackup.Status == BackupReadStatus.Missing
                    ? "No user backup exists yet."
                    : $"The user backup is invalid and was left unchanged: {userBackup.Error}";
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.User,
                    userBackupPath,
                    explanation);
            }

            Directory.CreateDirectory(_backupDirectory);
            var request = new FullBackupRestoreRequest(
                BackupSchemaVersion,
                userBackup.Manifest!.BackupId,
                _clock.GetUtcNow().UtcDateTime);
            JsonFileStore.Write(
                GetRestoreRequestPath(),
                request,
                options: BackupFormat.JsonOptions);
            return new FullBackupResult(
                FullBackupStatus.RestoreScheduled,
                FullBackupType.User,
                userBackupPath,
                "The backup is verified and ready to restore when Noted closes.");
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                "The backup restore could not be prepared.");
        }
    }

    internal FullBackupResult ApplyPendingUserBackupRestore()
    {
        var requestPath = GetRestoreRequestPath();
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
                ? GetPendingImportPath(request.ImportId.Value)
                : GetBackupPath(FullBackupType.User);
            var restoreBackup = ReadBackup(restorePath, FullBackupType.User);
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
                var destinationPath = GetRestoreDestination(entry.LogicalPath, manifest);
                var bytes = ReadFileBytes(sourcePath);
                VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
                FileWriter.WriteAllBytes(destinationPath, bytes);
                var restoredBytes = ReadFileBytes(destinationPath);
                VerifyFileBytes(restoredBytes, entry.Length, entry.Sha256, entry.LogicalPath);
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
                    DeleteImportDirectory(restoredImportPath);
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

    internal void CancelPendingUserBackupRestore()
    {
        var requestPath = GetRestoreRequestPath();
        var requestResult = JsonFileStore.Read<FullBackupRestoreRequest>(
            requestPath,
            BackupFormat.JsonOptions);
        FileWriter.DeleteIfExists(requestPath);
        if (requestResult.Success && requestResult.Value?.ImportId is Guid importId)
            TryDeleteImportDirectory(GetPendingImportPath(importId));
    }

    private FullBackupResult CreateBackup(
        FullBackupType requestedType,
        TimeSpan? minimumAge)
    {
        if (_backupCreatedThisRun)
        {
            return new FullBackupResult(
                FullBackupStatus.Skipped,
                null,
                null,
                "A full backup was already created during this application run.");
        }

        try
        {
            var interruptedWork = FindInterruptedWork();
            if (interruptedWork.Count > 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    null,
                    null,
                    "Backup creation is blocked because unfinished backup work needs review: " +
                    string.Join(", ", interruptedWork.Select(Path.GetFileName)));
            }

            var userBackupPath = GetBackupPath(FullBackupType.User);
            var recentBackupPath = GetBackupPath(FullBackupType.Recent);
            var userBackup = ReadBackup(userBackupPath, FullBackupType.User);
            var recentBackup = ReadBackup(recentBackupPath, FullBackupType.Recent);
            var invalidBackup = new[] { userBackup, recentBackup }
                .FirstOrDefault(backup => backup.Status == BackupReadStatus.Invalid);
            if (invalidBackup is not null)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    invalidBackup.Type,
                    invalidBackup.Path,
                    $"The {GetBackupName(invalidBackup.Type)} is invalid and was left unchanged: {invalidBackup.Error}");
            }

            if (requestedType == FullBackupType.User)
            {
                if (userBackup.Status == BackupReadStatus.Missing &&
                    recentBackup.Status == BackupReadStatus.Valid)
                {
                    return new FullBackupResult(
                        FullBackupStatus.Blocked,
                        FullBackupType.User,
                        userBackupPath,
                        "The user backup cannot be created because a recent automatic backup exists without its expected user backup. The existing data was left unchanged for review.");
                }
            }
            else if (userBackup.Status != BackupReadStatus.Valid)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.Recent,
                    recentBackupPath,
                    "Create and verify the user backup before creating a recent automatic backup.");
            }

            if (requestedType == FullBackupType.Recent && minimumAge.HasValue)
            {
                var newestBackup = recentBackup.Manifest ?? userBackup.Manifest;
                if (newestBackup is not null)
                {
                    var backupAge = _clock.GetUtcNow().UtcDateTime - newestBackup.CreatedUtc.ToUniversalTime();
                    if (backupAge < minimumAge.Value)
                    {
                        return new FullBackupResult(
                            FullBackupStatus.Skipped,
                            newestBackup.Type,
                            GetBackupPath(newestBackup.Type),
                            "The recent automatic backup is not due yet.");
                    }
                }
            }

            var backupFiles = _contentCatalog.Collect();
            if (backupFiles.Count == 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Skipped,
                    null,
                    null,
                    "No user data was available for a full backup.");
            }

            var backupType = requestedType;
            var existingBackupManifest = backupType == FullBackupType.User
                ? userBackup.Manifest
                : recentBackup.Manifest;
            if (existingBackupManifest is not null &&
                BackupFilesMatch(existingBackupManifest.Entries, backupFiles))
            {
                return new FullBackupResult(
                    FullBackupStatus.Skipped,
                    backupType,
                    GetBackupPath(backupType),
                    backupType == FullBackupType.User
                        ? "Your backup already matches the current verified data."
                        : "The recent automatic backup already matches the current verified data.");
            }

            var comparisonBackup = backupType == FullBackupType.User
                ? userBackup.Manifest
                : recentBackup.Manifest ?? userBackup.Manifest;
            var anomaly = FindSuspiciousDataLoss(comparisonBackup?.Entries, backupFiles);
            if (anomaly is not null)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    backupType,
                    GetBackupPath(backupType),
                    $"The backup update was blocked because the current data changed suspiciously: {anomaly}");
            }

            var buildResult = BuildVerifiedBackup(backupType, backupFiles);
            var replaceResult = ReplaceBackupFolder(buildResult.Path, backupType);
            _backupCreatedThisRun = true;
            return new FullBackupResult(
                FullBackupStatus.Created,
                backupType,
                replaceResult.Path,
                backupType == FullBackupType.User
                    ? userBackup.Status == BackupReadStatus.Valid
                        ? "Updated your verified full backup."
                        : "Created your verified full backup."
                    : "Updated the recent automatic backup.",
                replaceResult.Warning);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                null,
                null,
                "The full backup could not be created.");
        }
    }

    private BackupBuildResult BuildVerifiedBackup(
        FullBackupType backupType,
        IReadOnlyList<BackupSourceEntry> backupFiles)
    {
        Directory.CreateDirectory(_backupDirectory);
        var buildPath = Path.Combine(
            _backupDirectory,
            $".building-{Guid.NewGuid():N}");
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

        var verification = ReadBackup(buildPath, backupType);
        if (verification.Status != BackupReadStatus.Valid)
        {
            throw new FileVerificationException(
                $"The new full backup was built but could not be verified: {verification.Error}");
        }

        return new BackupBuildResult(buildPath);
    }

    private BackupReplaceResult ReplaceBackupFolder(string buildPath, FullBackupType backupType)
    {
        var backupPath = GetBackupPath(backupType);
        if (!Directory.Exists(backupPath))
        {
            Directory.Move(buildPath, backupPath);
            VerifyBackupFolder(backupPath, backupType);
            return new BackupReplaceResult(backupPath, null);
        }

        var retiredPath = Path.Combine(
            _backupDirectory,
            $".replacing-{GetBackupFolderName(backupType)}-{Guid.NewGuid():N}");
        Directory.Move(backupPath, retiredPath);
        try
        {
            Directory.Move(buildPath, backupPath);
            VerifyBackupFolder(backupPath, backupType);
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
            VerifyBackupFolder(previousPath, FullBackupType.User);
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

    private void VerifyBackupFolder(string path, FullBackupType backupType)
    {
        var verification = ReadBackup(path, backupType);
        if (verification.Status != BackupReadStatus.Valid)
        {
            throw new FileVerificationException(
                $"The saved {GetBackupName(backupType)} could not be verified: {verification.Error}");
        }
    }

    private BackupReadResult ReadBackup(string path, FullBackupType backupType)
    {
        if (!Directory.Exists(path))
            return new BackupReadResult(BackupReadStatus.Missing, backupType, path, null, null);

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return InvalidBackup(backupType, path, "The backup directory is a filesystem link.");

            var rootFiles = Directory.EnumerateFiles(path)
                .Select(Path.GetFileName)
                .ToList();
            var rootDirectories = Directory.EnumerateDirectories(path)
                .Select(Path.GetFileName)
                .ToList();
            if (rootFiles.Count != 1 ||
                !rootFiles[0]!.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                rootDirectories.Count != 1 ||
                !rootDirectories[0]!.Equals(FilesDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return InvalidBackup(
                    backupType,
                    path,
                    "The backup contains unexpected top-level files or directories.");
            }

            var manifestResult = JsonFileStore.Read<FullBackupManifest>(
                Path.Combine(path, ManifestFileName),
                BackupFormat.JsonOptions);
            if (!manifestResult.Success)
            {
                return new BackupReadResult(
                    BackupReadStatus.Invalid,
                    backupType,
                    path,
                    null,
                    "The backup manifest is missing, unreadable, or invalid.");
            }

            var manifest = manifestResult.Value!;
            if (manifest.SchemaVersion != BackupSchemaVersion)
                return InvalidBackup(backupType, path, "The backup schema is not supported.");
            if (manifest.Type != backupType)
                return InvalidBackup(backupType, path, "The backup type does not match its directory.");
            if (manifest.BackupId == Guid.Empty)
                return InvalidBackup(backupType, path, "The backup ID is missing.");
            if (manifest.Entries is null || manifest.Entries.Count == 0)
                return InvalidBackup(backupType, path, "The backup contains no files.");

            if (manifest.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.LogicalPath)))
                return InvalidBackup(backupType, path, "The backup contains an empty file entry or path.");

            var duplicate = manifest.Entries
                .GroupBy(entry => entry.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return InvalidBackup(backupType, path, $"The manifest repeats '{duplicate.Key}'.");

            var filesDirectory = Path.Combine(path, FilesDirectoryName);
            if ((File.GetAttributes(filesDirectory) & FileAttributes.ReparsePoint) != 0)
                return InvalidBackup(backupType, path, "The backup files directory is a filesystem link.");

            var expectedPaths = new HashSet<string>(
                manifest.Entries.Select(entry => NormalizeLogicalPath(entry.LogicalPath)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (entry.Length < 0 || string.IsNullOrWhiteSpace(entry.Sha256))
                    return InvalidBackup(backupType, path, $"The manifest entry '{entry.LogicalPath}' is incomplete.");

                _ = GetRestoreDestination(entry.LogicalPath, manifest);

                var filePath = GetContainedPath(filesDirectory, entry.LogicalPath);
                if (!File.Exists(filePath))
                    return InvalidBackup(backupType, path, $"The backup file '{entry.LogicalPath}' is missing.");

                var bytes = ReadFileBytes(filePath);
                if (bytes.LongLength != entry.Length ||
                    !string.Equals(
                        Convert.ToHexString(SHA256.HashData(bytes)),
                        entry.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return InvalidBackup(backupType, path, $"The backup file '{entry.LogicalPath}' failed hash verification.");
                }
            }

            var actualPaths = Directory.Exists(filesDirectory)
                ? EnumerateFilesWithoutLinks(filesDirectory)
                    .Select(file => NormalizeLogicalPath(Path.GetRelativePath(filesDirectory, file)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            if (!expectedPaths.SetEquals(actualPaths))
                return InvalidBackup(backupType, path, "The backup file list does not match its manifest.");

            return new BackupReadResult(
                BackupReadStatus.Valid,
                backupType,
                path,
                manifest,
                null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return InvalidBackup(backupType, path, "The selected backup could not be read or verified.");
        }
    }

    private static BackupReadResult InvalidBackup(
        FullBackupType backupType,
        string path,
        string error) =>
        new(BackupReadStatus.Invalid, backupType, path, null, error);

    private IReadOnlyList<string> FindInterruptedWork()
    {
        if (!Directory.Exists(_backupDirectory))
            return [];

        return Directory
            .EnumerateDirectories(_backupDirectory)
            .Where(path =>
                Path.GetFileName(path).StartsWith(".building-", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith(".replacing-", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith(".failed-", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith(".importing-", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith("pending-import-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindSuspiciousDataLoss(
        IReadOnlyList<FullBackupEntry>? previous,
        IReadOnlyList<BackupSourceEntry> current)
    {
        if (previous is null || previous.Count == 0)
            return null;

        var previousByPath = previous.ToDictionary(
            entry => NormalizeLogicalPath(entry.LogicalPath),
            StringComparer.OrdinalIgnoreCase);
        var currentByPath = current.ToDictionary(
            entry => NormalizeLogicalPath(entry.LogicalPath),
            StringComparer.OrdinalIgnoreCase);

        var missingMainFiles = new[]
            {
                "app/settings.json",
                "app/checklist.json",
                "app/checklist-tabs.json",
                "app/dictionary.json",
                "app/scratchpad.rtf"
            }
            .Where(path => previousByPath.ContainsKey(path) && !currentByPath.ContainsKey(path))
            .ToList();
        if (missingMainFiles.Count > 0)
            return $"previously backed-up files are missing: {string.Join(", ", missingMainFiles)}";

        foreach (var path in new[]
                 {
                     "app/checklist.json",
                     "app/checklist-tabs.json",
                     "app/dictionary.json"
                 })
        {
            if (previousByPath.TryGetValue(path, out var oldEntry) &&
                currentByPath.TryGetValue(path, out var newEntry) &&
                oldEntry.ItemCount > 0 &&
                newEntry.ItemCount == 0)
            {
                return $"'{path}' changed from {oldEntry.ItemCount} records to an empty collection";
            }
        }

        var previousNotes = previousByPath.Keys.Count(path => path.StartsWith("notes/", StringComparison.OrdinalIgnoreCase));
        var currentNotes = currentByPath.Keys.Count(path => path.StartsWith("notes/", StringComparison.OrdinalIgnoreCase));
        if (previousNotes >= 4 && currentNotes * 2 < previousNotes)
            return $"the note count dropped from {previousNotes} to {currentNotes}";

        var emptiedFiles = previousByPath
            .Where(pair =>
                pair.Value.ContentLength > 0 &&
                currentByPath.TryGetValue(pair.Key, out var currentEntry) &&
                currentEntry.ContentLength == 0)
            .Select(pair => pair.Key)
            .Take(3)
            .ToList();
        if (emptiedFiles.Count >= 2)
            return $"multiple previously non-empty files became empty: {string.Join(", ", emptiedFiles)}";

        var previousContentLength = previous.Sum(entry => Math.Max(0, entry.ContentLength ?? 0));
        var currentContentLength = current.Sum(entry => Math.Max(0, entry.ContentLength ?? 0));
        if (previousContentLength >= 4096 && currentContentLength * 4 < previousContentLength)
        {
            return $"total backed-up content dropped from {previousContentLength} bytes or characters to {currentContentLength}";
        }

        return null;
    }

    private static bool BackupFilesMatch(
        IReadOnlyList<FullBackupEntry> previous,
        IReadOnlyList<BackupSourceEntry> current)
    {
        if (previous.Count != current.Count)
            return false;

        var currentByPath = current.ToDictionary(
            entry => NormalizeLogicalPath(entry.LogicalPath),
            StringComparer.OrdinalIgnoreCase);
        return previous.All(entry =>
            currentByPath.TryGetValue(NormalizeLogicalPath(entry.LogicalPath), out var currentEntry) &&
            entry.Length == currentEntry.Length &&
            string.Equals(entry.Sha256, currentEntry.Sha256, StringComparison.OrdinalIgnoreCase));
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

        var copiedBytes = ReadFileBytes(destinationPath);
        var copiedHash = Convert.ToHexString(SHA256.HashData(copiedBytes));
        if (copiedBytes.LongLength != source.Length ||
            !string.Equals(copiedHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileVerificationException(
                $"'{source.SourcePath}' changed or could not be copied exactly while the full backup was being created.");
        }
    }

    private static byte[] ReadFileBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
            throw new IOException($"'{path}' is too large for the current backup implementation.");

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private string GetBackupPath(FullBackupType backupType) =>
        Path.Combine(
            _backupDirectory,
            GetBackupFolderName(backupType));

    // These folder names predate the User/Recent terminology and must remain readable.
    private static string GetBackupFolderName(FullBackupType backupType) =>
        backupType switch
        {
            FullBackupType.User => "protected",
            FullBackupType.Recent => "latest",
            _ => throw new ArgumentOutOfRangeException(nameof(backupType))
        };

    private string GetRestoreRequestPath() =>
        Path.Combine(_backupDirectory, RestoreRequestFileName);

    private string GetRestoreDestination(
        string logicalPath,
        FullBackupManifest manifest)
    {
        var normalizedPath = NormalizeLogicalPath(logicalPath);
        const string appPrefix = "app/";
        const string notesPrefix = "notes/";
        if (normalizedPath.StartsWith(appPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return GetContainedPath(
                _appDataDirectory,
                normalizedPath[appPrefix.Length..]);
        }

        if (normalizedPath.StartsWith(notesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return GetContainedPath(
                manifest.NotesRoot,
                normalizedPath[notesPrefix.Length..]);
        }

        throw new FileVerificationException(
            $"The backup path '{logicalPath}' does not identify application or note data.");
    }

    private BackupFileComparison CompareWithCurrentFile(
        FullBackupEntry entry,
        FullBackupManifest manifest)
    {
        try
        {
            var currentPath = GetRestoreDestination(entry.LogicalPath, manifest);
            var bytes = ReadFileBytes(currentPath);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return bytes.LongLength == entry.Length &&
                   string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                ? BackupFileComparison.Unchanged
                : BackupFileComparison.Changed;
        }
        catch (FileNotFoundException)
        {
            return BackupFileComparison.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return BackupFileComparison.Missing;
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return BackupFileComparison.Unavailable;
        }
    }

    private static void VerifyFileBytes(
        byte[] bytes,
        long expectedLength,
        string expectedHash,
        string logicalPath)
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.LongLength != expectedLength ||
            !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileVerificationException(
                $"The restored file '{logicalPath}' did not match the verified backup.");
        }
    }

    private static string GetBackupName(FullBackupType backupType) =>
        backupType switch
        {
            FullBackupType.User => "user backup",
            FullBackupType.Recent => "recent automatic backup",
            FullBackupType.BeforeRestore => "before-restore backup",
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

    private enum BackupReadStatus
    {
        Missing,
        Valid,
        Invalid
    }

    private sealed record BackupReadResult(
        BackupReadStatus Status,
        FullBackupType Type,
        string Path,
        FullBackupManifest? Manifest,
        string? Error);

    private sealed record BackupBuildResult(string Path);

    private sealed record BackupReplaceResult(string Path, string? Warning);
}
