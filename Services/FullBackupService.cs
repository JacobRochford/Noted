using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly BackupArchiveWriter _archiveWriter;
    private readonly BackupRestore _backupRestore;
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
        _archiveWriter = new BackupArchiveWriter(
            _appDataDirectory,
            _notesDirectory,
            _backupDirectory,
            _clock,
            _archiveReader);
        _backupRestore = new BackupRestore(
            _backupDirectory,
            _archiveReader,
            GetRestoreDestination,
            DeleteImportDirectory);
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
        _archiveWriter.UpdateNotesDirectory(_notesDirectory);
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

    internal FullBackupResult ApplyPendingUserBackupRestore() =>
        _backupRestore.ApplyPendingUserBackupRestore();

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

            var buildResult = _archiveWriter.BuildVerifiedBackup(backupType, backupFiles);
            var replaceResult = _archiveWriter.ReplaceBackupFolder(buildResult.Path, backupType);
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

    private static bool IsExpectedBackupException(Exception exception) =>
        FileSystemErrors.IsExpected(exception) || exception is JsonException or InvalidDataException;

}
