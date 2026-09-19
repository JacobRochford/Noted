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
    private readonly string _appDataDirectory;
    private string _notesDirectory;
    private readonly string _backupDirectory;
    private readonly BackupContentCatalog _contentCatalog;
    private readonly BackupArchiveReader _archiveReader;
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
        _archiveReader = new BackupArchiveReader(
            GetRestoreDestination,
            TransformImportedFile,
            CreateImportedManifest);
        _clock = clock ?? TimeProvider.System;
        TryCleanupFinishedImportFolders();
    }

    internal string BackupDirectory => _backupDirectory;

    internal bool UserBackupExists =>
        Directory.Exists(GetBackupPath(FullBackupType.User));

    internal FullBackupInfo GetUserBackupInfo() =>
        _archiveReader.GetBackupInfo(GetBackupPath(FullBackupType.User), FullBackupType.User);

    internal FullBackupPreview GetUserBackupPreview() =>
        _archiveReader.GetBackupPreview(GetBackupPath(FullBackupType.User), FullBackupType.User);

    internal BackupFileContent ReadUserBackupFile(
        Guid backupId,
        BackupFileSummary file) =>
        _archiveReader.ReadBackupFile(
            GetBackupPath(FullBackupType.User),
            FullBackupType.User,
            backupId,
            file);
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
            var userBackup = _archiveReader.ReadBackup(userBackupPath, FullBackupType.User);
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
                var destinationPath = GetRestoreDestination(entry.LogicalPath, manifest);
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
            var userBackup = _archiveReader.ReadBackup(userBackupPath, FullBackupType.User);
            var recentBackup = _archiveReader.ReadBackup(recentBackupPath, FullBackupType.Recent);
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

        var verification = _archiveReader.ReadBackup(buildPath, backupType);
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

    private static string GetBackupName(FullBackupType backupType) =>
        backupType switch
        {
            FullBackupType.User => "user backup",
            FullBackupType.Recent => "recent automatic backup",
            FullBackupType.BeforeRestore => "before-restore backup",
            _ => throw new ArgumentOutOfRangeException(nameof(backupType))
        };

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        var directoryWithSeparator = Path.EndsInDirectorySeparator(fullDirectory)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

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

    private sealed record BackupBuildResult(string Path);

    private sealed record BackupReplaceResult(string Path, string? Warning);
}
