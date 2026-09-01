using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noted.Models;

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
    Recent
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
    BackupFileComparison Comparison);

internal sealed record FullBackupPreview(
    bool Exists,
    bool IsValid,
    Guid BackupId,
    DateTime? CreatedUtc,
    IReadOnlyList<BackupFileSummary> Files,
    string? Error);

internal sealed record BackupFileContent(
    bool Success,
    bool NeedsRecheck,
    BackupContentFormat Format,
    byte[]? Data,
    string? Error);

internal sealed partial class FullBackupService
{
    private const int BackupSchemaVersion = 1;
    private const int SupportedSettingsSchemaVersion = 1;
    private const string ManifestFileName = "manifest.json";
    private const string FilesDirectoryName = "files";
    private const string RestoreRequestFileName = "restore-request.json";
    private const long MaximumPreviewFileSize = 4 * 1024 * 1024;
    private static readonly Guid MiniPadDraftId =
        new("A6CB4208-79C0-4C08-9D07-9CDF33F31337");
    private static readonly JsonSerializerOptions BackupJsonOptions = CreateJsonOptions();

    private readonly string _appDataDirectory;
    private string _notesDirectory;
    private readonly string _backupDirectory;
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
        _clock = clock ?? TimeProvider.System;
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
            BackupJsonOptions);
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
            return new BackupFileContent(
                false,
                true,
                BackupContentFormat.Text,
                null,
                $"The selected file could not be verified: {ex.Message}");
        }
    }

    internal void UpdateNotesDirectory(string notesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
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
                options: BackupJsonOptions);
            return new FullBackupResult(
                FullBackupStatus.RestoreScheduled,
                FullBackupType.User,
                userBackupPath,
                "The backup is verified and ready to restore when Noted closes.");
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                $"The backup restore could not be prepared: {ex.Message}");
        }
    }

    internal FullBackupResult ApplyPendingUserBackupRestore()
    {
        var requestPath = GetRestoreRequestPath();
        var requestResult = JsonFileStore.Read<FullBackupRestoreRequest>(
            requestPath,
            BackupJsonOptions);
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

            var userBackupPath = GetBackupPath(FullBackupType.User);
            var userBackup = ReadBackup(userBackupPath, FullBackupType.User);
            if (userBackup.Status != BackupReadStatus.Valid)
            {
                throw new FileVerificationException(
                    $"The user backup cannot be restored: {userBackup.Error ?? "the backup is missing"}.");
            }

            var manifest = userBackup.Manifest!;
            if (manifest.BackupId != request.BackupId)
            {
                throw new FileVerificationException(
                    "The user backup changed after the restore was requested. Nothing was restored.");
            }

            var filesDirectory = Path.Combine(userBackupPath, FilesDirectoryName);
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
            return new FullBackupResult(
                FullBackupStatus.Restored,
                FullBackupType.User,
                userBackupPath,
                $"Restored {manifest.Entries.Count} files from your verified backup. The backup was not changed.");
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                $"The backup restore did not finish: {ex.Message} The backup and restore request were left unchanged so Noted can retry.");
        }
    }

    internal void CancelPendingUserBackupRestore() =>
        FileWriter.DeleteIfExists(GetRestoreRequestPath());

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

            var backupFiles = CollectBackupFiles();
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
            System.Diagnostics.Debug.WriteLine(ex);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                null,
                null,
                $"The full backup could not be created: {ex.Message}");
        }
    }

    private IReadOnlyList<BackupSourceEntry> CollectBackupFiles()
    {
        if (!Directory.Exists(_notesDirectory))
            throw new DirectoryNotFoundException($"The notes folder '{_notesDirectory}' could not be found.");

        var sources = new List<BackupSourceEntry>();
        AddAppFile(sources, "settings.json", ValidateSettings);
        AddAppFile<List<ChecklistItemState>>(
            sources,
            "checklist.json",
            ValidateCollection<List<ChecklistItemState>>,
            items => items.Count);
        AddAppFile<List<ChecklistTabState>>(
            sources,
            "checklist-tabs.json",
            ValidateCollection<List<ChecklistTabState>>,
            items => items.Count);
        AddAppFile<List<DictionaryItemState>>(
            sources,
            "dictionary.json",
            ValidateCollection<List<DictionaryItemState>>,
            items => items.Count);
        AddAppTextFile(sources, "scratchpad.rtf");
        AddAppFile<NoteEditorSession>(
            sources,
            Path.Combine("session", "editor-workspace.json"),
            ValidateEditorSession);
        AddNoteRecoveryFiles(sources);
        AddNoteHistoryFiles(sources);
        AddDeletedNotes(sources);
        AddNotes(sources);

        var duplicate = sources
            .GroupBy(source => source.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new IOException($"Multiple source files map to '{duplicate.Key}'.");

        return sources
            .OrderBy(source => source.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddAppFile(
        ICollection<BackupSourceEntry> sources,
        string relativePath,
        Func<byte[], BackupContentDetails> validator)
    {
        var sourcePath = Path.Combine(_appDataDirectory, relativePath);
        if (!File.Exists(sourcePath))
            return;

        sources.Add(CreateSourceEntry(
            ToLogicalPath("app", relativePath),
            sourcePath,
            validator));
    }

    private void AddAppFile<T>(
        ICollection<BackupSourceEntry> sources,
        string relativePath,
        Func<byte[], T> validator,
        Func<T, int>? itemCount = null)
    {
        AddAppFile(
            sources,
            relativePath,
            bytes =>
            {
                var value = validator(bytes);
                return new BackupContentDetails(
                    itemCount?.Invoke(value),
                    null);
            });
    }

    private void AddAppTextFile(
        ICollection<BackupSourceEntry> sources,
        string relativePath)
    {
        AddAppFile(
            sources,
            relativePath,
            bytes => new BackupContentDetails(null, bytes.LongLength));
    }

    private void AddNoteRecoveryFiles(ICollection<BackupSourceEntry> sources)
    {
        var recoveryDirectory = Path.Combine(_appDataDirectory, "recovery");
        if (!Directory.Exists(recoveryDirectory))
            return;

        ThrowIfDirectoryIsLink(recoveryDirectory);
        foreach (var path in Directory.EnumerateFiles(
                     recoveryDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            AddRecoveryFile(sources, path, bytes => ValidateNoteDraft(bytes, path));
        }

        var miniPadDirectory = Path.Combine(recoveryDirectory, "micro-scratchpads");
        if (Directory.Exists(miniPadDirectory))
            ThrowIfDirectoryIsLink(miniPadDirectory);

        var miniPadPath = Path.Combine(miniPadDirectory, $"{MiniPadDraftId:N}.json");
        if (File.Exists(miniPadPath))
            AddRecoveryFile(sources, miniPadPath, bytes => ValidateMiniPadDraft(bytes, miniPadPath));
    }

    private void AddRecoveryFile(
        ICollection<BackupSourceEntry> sources,
        string path,
        Func<byte[], BackupContentDetails> validator)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The recovery file '{path}' is a filesystem link and cannot be included safely.");

        var relativePath = Path.GetRelativePath(_appDataDirectory, path);
        sources.Add(CreateSourceEntry(
            ToLogicalPath("app", relativePath),
            path,
            validator));
    }

    private static void ThrowIfDirectoryIsLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The recovery folder '{path}' is a filesystem link and cannot be included safely.");
    }

    private void AddNoteHistoryFiles(ICollection<BackupSourceEntry> sources)
    {
        var historyDirectory = Path.Combine(_appDataDirectory, "note-history");
        if (!Directory.Exists(historyDirectory))
            return;

        foreach (var path in EnumerateFilesWithoutLinks(historyDirectory))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(_appDataDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath("app", relativePath),
                path,
                ValidateNoteHistoryEntry));
        }
    }

    private void AddDeletedNotes(ICollection<BackupSourceEntry> sources)
    {
        var deletedNotesDirectory = Path.Combine(_appDataDirectory, "DeletedNotes");
        if (!Directory.Exists(deletedNotesDirectory))
            return;

        foreach (var path in EnumerateFilesWithoutLinks(deletedNotesDirectory))
        {
            if (!NoteFileExtensions.IsSupported(path))
                continue;

            var relativePath = Path.GetRelativePath(_appDataDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath("app", relativePath),
                path,
                bytes => new BackupContentDetails(null, bytes.LongLength)));
        }
    }

    private void AddNotes(ICollection<BackupSourceEntry> sources)
    {
        foreach (var path in EnumerateFilesWithoutLinks(
                     _notesDirectory,
                     [_backupDirectory]))
        {
            if (!NoteFileExtensions.IsSupported(path))
                continue;

            var relativePath = Path.GetRelativePath(_notesDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath("notes", relativePath),
                path,
                bytes => new BackupContentDetails(null, bytes.LongLength)));
        }
    }

    private static BackupSourceEntry CreateSourceEntry(
        string logicalPath,
        string sourcePath,
        Func<byte[], BackupContentDetails> validator)
    {
        var bytes = ReadFileBytes(sourcePath);
        var details = validator(bytes);
        return new BackupSourceEntry(
            logicalPath,
            Path.GetFullPath(sourcePath),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            details.ItemCount,
            details.ContentLength);
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
            options: BackupJsonOptions);

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
            System.Diagnostics.Debug.WriteLine(ex);
            return new BackupReplaceResult(
                backupPath,
                $"The new recent backup is verified, but the retired recent backup could not be removed from '{retiredPath}': {ex.Message}");
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
            if (!Directory.Exists(previousPath) &&
                olderPreviousPath is not null &&
                Directory.Exists(olderPreviousPath))
            {
                Directory.Move(olderPreviousPath, previousPath);
                olderPreviousPath = null;
            }

            return new BackupReplaceResult(
                userBackupPath,
                $"The updated backup is verified, but the previous backup could not be filed normally. Its data was left unchanged for review: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine(ex);
            return new BackupReplaceResult(
                userBackupPath,
                $"The current and previous backups are verified, but an older backup folder could not be removed from '{olderPreviousPath}': {ex.Message}");
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
                BackupJsonOptions);
            if (!manifestResult.Success)
            {
                return new BackupReadResult(
                    BackupReadStatus.Invalid,
                    backupType,
                    path,
                    null,
                    manifestResult.Error?.Message ?? "The backup manifest is missing or invalid.");
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
            return InvalidBackup(backupType, path, ex.Message);
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
                Path.GetFileName(path).StartsWith(".failed-", StringComparison.OrdinalIgnoreCase))
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

    private static BackupContentDetails ValidateSettings(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The Settings file does not contain a JSON object.");

        if (document.RootElement.TryGetProperty("SchemaVersion", out var schemaElement))
        {
            if (!schemaElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion < 0 ||
                schemaVersion > SupportedSettingsSchemaVersion)
            {
                throw new JsonException("The Settings schema is not supported.");
            }
        }

        if (document.RootElement.TryGetProperty("Revision", out var revisionElement) &&
            (!revisionElement.TryGetInt64(out var revision) || revision < 0))
        {
            throw new JsonException("The Settings revision is invalid.");
        }

        return new BackupContentDetails(null, null);
    }

    private static T ValidateCollection<T>(byte[] bytes)
        where T : class
    {
        var value = JsonSerializer.Deserialize<T>(bytes)
            ?? throw new JsonException("The JSON collection contains no value.");
        if (value is System.Collections.IEnumerable items &&
            items.Cast<object?>().Any(item => item is null))
        {
            throw new JsonException("The JSON collection contains an empty item.");
        }

        return value;
    }

    private static NoteEditorSession ValidateEditorSession(byte[] bytes)
    {
        var session = JsonSerializer.Deserialize<NoteEditorSession>(bytes)
            ?? throw new JsonException("The editor session contains no value.");
        if (session.Tabs is null)
            throw new JsonException("The editor session does not contain a tab list.");
        return session;
    }

    private static BackupContentDetails ValidateMiniPadDraft(byte[] bytes, string path)
    {
        var draft = JsonSerializer.Deserialize<MiniPadRecoveryDraft>(bytes)
            ?? throw new JsonException("The MiniPad recovery file contains no value.");
        if (draft.Id != MiniPadDraftId ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                draft.Id.ToString("N"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("The MiniPad recovery ID does not match its file name.");
        }

        return new BackupContentDetails(null, draft.Content?.Length ?? 0);
    }

    private static BackupContentDetails ValidateNoteDraft(byte[] bytes, string path)
    {
        var draft = JsonSerializer.Deserialize<NoteRecoveryDraft>(bytes)
            ?? throw new JsonException("The note recovery file contains no value.");
        if (string.IsNullOrWhiteSpace(draft.FilePath) ||
            !NoteFileExtensions.IsSupported(draft.FilePath))
        {
            throw new JsonException("The note recovery file does not contain a supported note path.");
        }

        if (!Path.GetFileName(path).Equals("editor-draft.json", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedPath = Path.GetFullPath(draft.FilePath).ToUpperInvariant();
            var expectedName = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath))) + ".json";
            if (!Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("The note path does not match the recovery file name.");
        }

        return new BackupContentDetails(null, draft.Content?.Length ?? 0);
    }

    private static BackupContentDetails ValidateNoteHistoryEntry(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("SchemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) ||
            schemaVersion != 1 ||
            !root.TryGetProperty("NotePath", out var notePath) ||
            notePath.ValueKind != JsonValueKind.String ||
            !NoteFileExtensions.IsSupported(notePath.GetString() ?? string.Empty) ||
            !root.TryGetProperty("Content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("SavedUtc", out var savedUtc) ||
            savedUtc.ValueKind != JsonValueKind.String ||
            !savedUtc.TryGetDateTime(out _))
        {
            throw new JsonException("The saved-note history entry is invalid.");
        }

        return new BackupContentDetails(null, content.GetString()?.Length ?? 0);
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

    private static IEnumerable<string> EnumerateFilesWithoutLinks(
        string rootDirectory,
        IReadOnlyCollection<string>? excludedDirectories = null)
    {
        var exclusions = (excludedDirectories ?? [])
            .Select(Path.GetFullPath)
            .ToList();
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (exclusions.Any(exclusion => PathsEqual(fullRoot, exclusion) || IsPathWithin(fullRoot, exclusion)))
            yield break;

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The source file '{path}' is a filesystem link and cannot be included safely.");
                yield return path;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The source folder '{child}' is a filesystem link and cannot be included safely.");
                if (exclusions.Any(exclusion => PathsEqual(child, exclusion) || IsPathWithin(child, exclusion)))
                    continue;
                pending.Push(child);
            }
        }
    }

    private string GetBackupPath(FullBackupType backupType) =>
        Path.Combine(
            _backupDirectory,
            GetBackupFolderName(backupType));

    // These folder names predate the User/Recent terminology and must remain readable.
    private static string GetBackupFolderName(FullBackupType backupType) =>
        backupType == FullBackupType.User ? "protected" : "latest";

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
            System.Diagnostics.Debug.WriteLine(ex);
            return BackupFileComparison.Unavailable;
        }
    }

    private static string GetDisplayPath(string logicalPath)
    {
        var normalizedPath = NormalizeLogicalPath(logicalPath);
        var separator = normalizedPath.IndexOf('/');
        return separator >= 0 && separator < normalizedPath.Length - 1
            ? normalizedPath[(separator + 1)..]
            : normalizedPath;
    }

    private static string GetFileCategory(string logicalPath)
    {
        var path = NormalizeLogicalPath(logicalPath);
        if (path.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
            return "Notes";
        if (path.StartsWith("app/DeletedNotes/", StringComparison.OrdinalIgnoreCase))
            return "Deleted notes";
        if (path.StartsWith("app/note-history/", StringComparison.OrdinalIgnoreCase))
            return "Note history";
        if (path.StartsWith("app/recovery/micro-scratchpads/", StringComparison.OrdinalIgnoreCase))
            return "MiniPad";
        if (path.StartsWith("app/recovery/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("app/session/editor-workspace.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Open notes";
        }
        if (path.Equals("app/checklist.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("app/checklist-tabs.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Checklist";
        }
        if (path.Equals("app/dictionary.json", StringComparison.OrdinalIgnoreCase))
            return "Dictionary";
        if (path.Equals("app/scratchpad.rtf", StringComparison.OrdinalIgnoreCase))
            return "Scratchpad";
        if (path.Equals("app/settings.json", StringComparison.OrdinalIgnoreCase))
            return "Settings";
        return "Application data";
    }

    private static BackupContentFormat GetContentFormat(string logicalPath)
    {
        var extension = Path.GetExtension(logicalPath);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return BackupContentFormat.Json;
        if (extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
            return BackupContentFormat.RichText;
        return BackupContentFormat.Text;
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
        backupType == FullBackupType.User ? "user backup" : "recent automatic backup";

    private static string GetContainedPath(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var platformPath = NormalizeLogicalPath(relativePath)
            .Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, platformPath));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The backup path '{relativePath}' escapes its backup directory.");
        return fullPath;
    }

    private static string ToLogicalPath(string prefix, string relativePath) =>
        $"{prefix}/{NormalizeLogicalPath(relativePath)}";

    private static string NormalizeLogicalPath(string path) =>
        path.Replace('\\', '/')
            .TrimStart('/');

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

    private static bool IsExpectedBackupException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record BackupSourceEntry(
        string LogicalPath,
        string SourcePath,
        long Length,
        string Sha256,
        int? ItemCount,
        long? ContentLength)
    {
        internal FullBackupEntry ToManifestEntry() =>
            new(LogicalPath, SourcePath, Length, Sha256, ItemCount, ContentLength);
    }

    private sealed record BackupContentDetails(int? ItemCount, long? ContentLength);

    private sealed record FullBackupManifest(
        int SchemaVersion,
        [property: JsonPropertyName("SnapshotId")]
        Guid BackupId,
        [property: JsonPropertyName("Role")]
        FullBackupType Type,
        DateTime CreatedUtc,
        string AppDataRoot,
        string NotesRoot,
        IReadOnlyList<FullBackupEntry> Entries);

    private sealed record FullBackupRestoreRequest(
        int SchemaVersion,
        [property: JsonPropertyName("SnapshotId")]
        Guid BackupId,
        DateTime RequestedUtc);

    private sealed record FullBackupEntry(
        string LogicalPath,
        string OriginalPath,
        long Length,
        string Sha256,
        int? ItemCount,
        long? ContentLength);

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
