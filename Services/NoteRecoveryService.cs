using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

public sealed class NoteRecoveryService : INoteRecoveryService
{
    private const string PrimaryFileSuffix = ".json";
    private const string BackupFileSuffix = ".json.bak";
    private readonly string _recoveryDirectory;
    private readonly string _legacyDraftFilePath;
    private readonly HashSet<string> _preserveBackupOnNextSave =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writesBlocked =
        new(StringComparer.OrdinalIgnoreCase);

    public NoteRecoveryService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _recoveryDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "recovery");
        _legacyDraftFilePath = Path.Combine(_recoveryDirectory, "editor-draft.json");
    }

    public NoteRecoveryDraftLoadResult LoadDraft(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalizedPath = Path.GetFullPath(filePath);
        var issues = new List<NoteRecoveryIssue>();
        var primaryPath = GetDraftFilePath(normalizedPath);
        var draft = LoadDraftPair(primaryPath, issues);
        if (draft is not null && PathsEqual(draft.FilePath, normalizedPath))
            return new NoteRecoveryDraftLoadResult(draft, issues);

        var legacyDraft = LoadDraftPair(_legacyDraftFilePath, issues);
        return new NoteRecoveryDraftLoadResult(
            legacyDraft is not null && PathsEqual(legacyDraft.FilePath, normalizedPath)
                ? legacyDraft
                : null,
            issues);
    }

    public NoteRecoveryLoadResult LoadDrafts()
    {
        if (!Directory.Exists(_recoveryDirectory))
            return new NoteRecoveryLoadResult([], []);

        var drafts = new Dictionary<string, NoteRecoveryDraft>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<NoteRecoveryIssue>();
        try
        {
            var primaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_recoveryDirectory))
            {
                if (TryGetPrimaryPath(path, out var primaryPath))
                    primaryPaths.Add(primaryPath);
            }

            foreach (var primaryPath in primaryPaths)
            {
                var draft = LoadDraftPair(primaryPath, issues);
                if (draft is not null &&
                    (!drafts.TryGetValue(draft.FilePath, out var existing) ||
                     draft.UpdatedUtc > existing.UpdatedUtc))
                {
                    drafts[draft.FilePath] = draft;
                }
            }
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new NoteRecoveryIssue(
                _recoveryDirectory,
                $"The note recovery folder could not be read: {ex.Message}"));
        }

        return new NoteRecoveryLoadResult(
            drafts.Values.OrderBy(draft => draft.UpdatedUtc).ToList(),
            issues);
    }

    public string? SaveDraft(string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var normalizedPath = Path.GetFullPath(filePath);
        if (!NoteFileExtensions.IsSupported(normalizedPath))
            throw new ArgumentException("Unsupported note file type.", nameof(filePath));

        var draftFilePath = GetDraftFilePath(normalizedPath);
        if (_writesBlocked.Contains(draftFilePath))
        {
            throw new IOException(
                $"Recovery data for '{Path.GetFileName(normalizedPath)}' cannot be saved because an existing recovery file could not be read or preserved. Resolve the reported file error and restart Noted before retrying.");
        }

        var existing = ReadDraft(draftFilePath).Draft;
        if (existing is not null &&
            PathsEqual(existing.FilePath, normalizedPath) &&
            string.Equals(existing.Content, content, StringComparison.Ordinal))
        {
            return null;
        }

        var preserveBackup = _preserveBackupOnNextSave.Contains(draftFilePath);
        FileWriteResult writeResult;
        try
        {
            writeResult = JsonFileStore.Write(
                draftFilePath,
                new NoteRecoveryDraft(normalizedPath, content, DateTime.UtcNow),
                preserveBackup ? null : GetBackupFilePath(draftFilePath));
        }
        catch (FileVerificationException)
        {
            _writesBlocked.Add(draftFilePath);
            throw;
        }

        if (preserveBackup)
            _preserveBackupOnNextSave.Remove(draftFilePath);
        return writeResult.Warning;
    }

    public void DeleteDraft(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalizedPath = Path.GetFullPath(filePath);
        var draftFilePath = GetDraftFilePath(normalizedPath);
        var failures = new List<Exception>();

        DeleteRecoveryFamily(draftFilePath, failures);

        var legacyDraft = ReadDraft(_legacyDraftFilePath);
        if (legacyDraft.Draft is not null && PathsEqual(legacyDraft.Draft.FilePath, normalizedPath))
            DeleteRecoveryFamily(_legacyDraftFilePath, failures);

        if (failures.Count > 0)
        {
            var details = string.Join(
                " ",
                failures.Select(failure => failure.Message).Distinct(StringComparer.Ordinal));
            throw new IOException(
                $"One or more recovery files for '{Path.GetFileName(normalizedPath)}' could not be deleted. {details}",
                new AggregateException(failures));
        }

        _preserveBackupOnNextSave.Remove(draftFilePath);
        _writesBlocked.Remove(draftFilePath);
    }

    private NoteRecoveryDraft? LoadDraftPair(
        string primaryPath,
        ICollection<NoteRecoveryIssue> issues)
    {
        var primary = ReadDraft(primaryPath);
        if (primary.Draft is not null)
            return primary.Draft;

        var backupPath = GetBackupFilePath(primaryPath);
        var backup = ReadDraft(backupPath);
        if (backup.Draft is not null)
        {
            var preservedPath = PreserveCorruptFile(primaryPath, primary.Status, issues);
            if (primary.Status == JsonFileReadStatus.Unavailable ||
                (primary.Status == JsonFileReadStatus.Corrupt && preservedPath is null))
            {
                _preserveBackupOnNextSave.Add(primaryPath);
            }

            issues.Add(new NoteRecoveryIssue(
                primaryPath,
                primary.Status == JsonFileReadStatus.Missing
                    ? $"The recovery draft for '{Path.GetFileName(backup.Draft.FilePath)}' was restored from its backup because the primary file was missing."
                    : $"The recovery draft for '{Path.GetFileName(backup.Draft.FilePath)}' was restored from its backup because the primary file could not be loaded."));
            return backup.Draft;
        }

        var primaryPreserved = PreserveCorruptFile(primaryPath, primary.Status, issues);
        var backupPreserved = PreserveCorruptFile(backupPath, backup.Status, issues);
        if (primary.Status == JsonFileReadStatus.Unavailable ||
            backup.Status == JsonFileReadStatus.Unavailable ||
            (primary.Status == JsonFileReadStatus.Corrupt && primaryPreserved is null) ||
            (backup.Status == JsonFileReadStatus.Corrupt && backupPreserved is null))
        {
            _writesBlocked.Add(primaryPath);
        }

        var failure = primary.Status != JsonFileReadStatus.Missing
            ? primary
            : backup;
        if (failure.Status != JsonFileReadStatus.Missing)
        {
            issues.Add(new NoteRecoveryIssue(
                failure.Path,
                $"A note recovery draft could not be restored: {failure.Error ?? "the recovery file is invalid."}"));
        }

        return null;
    }

    private DraftReadResult ReadDraft(string path)
    {
        var result = JsonFileStore.Read<NoteRecoveryDraft>(path);
        if (!result.Success)
            return new DraftReadResult(path, result.Status, null, result.Error?.Message);

        try
        {
            var draft = result.Value!;
            if (string.IsNullOrWhiteSpace(draft.FilePath) ||
                !NoteFileExtensions.IsSupported(draft.FilePath))
            {
                return new DraftReadResult(
                    path,
                    JsonFileReadStatus.Corrupt,
                    null,
                    "The recovery draft does not contain a supported note path.");
            }

            var normalizedDraft = draft with { FilePath = Path.GetFullPath(draft.FilePath) };
            var expectedPath = GetDraftFilePath(normalizedDraft.FilePath);
            if (!PathsEqual(path, _legacyDraftFilePath) &&
                !PathsEqual(path, GetBackupFilePath(_legacyDraftFilePath)) &&
                !PathsEqual(path, expectedPath) &&
                !PathsEqual(path, GetBackupFilePath(expectedPath)))
            {
                return new DraftReadResult(
                    path,
                    JsonFileReadStatus.Corrupt,
                    null,
                    "The note path inside the recovery draft does not match its file name.");
            }

            return new DraftReadResult(
                path,
                JsonFileReadStatus.Success,
                normalizedDraft,
                null);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            return new DraftReadResult(path, JsonFileReadStatus.Corrupt, null, ex.Message);
        }
    }

    private void DeleteRecoveryFamily(string primaryPath, ICollection<Exception> failures)
    {
        DeleteFile(primaryPath, failures);
        DeleteFile(GetBackupFilePath(primaryPath), failures);

        try
        {
            if (!Directory.Exists(_recoveryDirectory))
                return;

            foreach (var path in Directory.EnumerateFiles(
                         _recoveryDirectory,
                         $"{Path.GetFileName(primaryPath)}.corrupt-*"))
            {
                DeleteFile(path, failures);
            }

            foreach (var path in Directory.EnumerateFiles(
                         _recoveryDirectory,
                         $"{Path.GetFileName(GetBackupFilePath(primaryPath))}.corrupt-*"))
            {
                DeleteFile(path, failures);
            }
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            failures.Add(ex);
        }
    }

    private static string? PreserveCorruptFile(
        string path,
        JsonFileReadStatus status,
        ICollection<NoteRecoveryIssue> issues)
    {
        if (status != JsonFileReadStatus.Corrupt || !File.Exists(path))
            return null;

        var preservedPath =
            $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, preservedPath);
            issues.Add(new NoteRecoveryIssue(
                path,
                $"The corrupt note recovery file was preserved as '{Path.GetFileName(preservedPath)}'."));
            return preservedPath;
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new NoteRecoveryIssue(
                path,
                $"The corrupt note recovery file could not be preserved under a new name: {ex.Message}"));
            return null;
        }
    }

    private static void DeleteFile(string path, ICollection<Exception> failures)
    {
        try
        {
            FileWriter.DeleteIfExists(path);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            failures.Add(ex);
        }
    }

    private static bool TryGetPrimaryPath(string path, out string primaryPath)
    {
        if (path.EndsWith(BackupFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            primaryPath = path[..^".bak".Length];
            return true;
        }

        if (path.EndsWith(PrimaryFileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            primaryPath = path;
            return true;
        }

        primaryPath = string.Empty;
        return false;
    }

    private string GetDraftFilePath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return Path.Combine(_recoveryDirectory, $"{hash}.json");
    }

    private static string GetBackupFilePath(string primaryPath) =>
        $"{primaryPath}.bak";

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedRecoveryException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }

    private sealed record DraftReadResult(
        string Path,
        JsonFileReadStatus Status,
        NoteRecoveryDraft? Draft,
        string? Error);
}
