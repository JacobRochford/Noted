using System.IO;
using System.Text.Json;

namespace Noted.Services;

public sealed class MicroScratchpadRecoveryService : IMicroScratchpadRecoveryService
{
    private const string PrimaryFileSuffix = ".json";
    private const string BackupFileSuffix = ".json.bak";
    private readonly string _recoveryDirectory;
    private readonly HashSet<Guid> _preserveBackupOnNextSave = [];

    public MicroScratchpadRecoveryService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _recoveryDirectory = Path.Combine(
            Path.GetFullPath(storageDirectory),
            "recovery",
            "micro-scratchpads");
    }

    public MicroScratchpadRecoveryLoadResult LoadDrafts()
    {
        if (!Directory.Exists(_recoveryDirectory))
            return new MicroScratchpadRecoveryLoadResult([], []);

        var drafts = new List<MicroScratchpadRecoveryDraft>();
        var issues = new List<MicroScratchpadRecoveryIssue>();
        try
        {
            var draftIds = new HashSet<Guid>();
            foreach (var path in Directory.EnumerateFiles(_recoveryDirectory))
            {
                if (TryGetDraftId(path, out var id))
                {
                    draftIds.Add(id);
                    continue;
                }

                if (IsRecoveryFile(path))
                {
                    issues.Add(new MicroScratchpadRecoveryIssue(
                        path,
                        "The file name does not contain a valid Micro Scratchpad recovery ID."));
                }
            }

            foreach (var id in draftIds)
                LoadDraft(id, drafts, issues);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new MicroScratchpadRecoveryIssue(
                _recoveryDirectory,
                $"The recovery folder could not be read: {ex.Message}"));
        }

        return new MicroScratchpadRecoveryLoadResult(
            drafts.OrderBy(draft => draft.UpdatedUtc).ToList(),
            issues);
    }

    public void SaveDraft(Guid id, string content)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A recovery ID is required.", nameof(id));

        ArgumentNullException.ThrowIfNull(content);

        var draft = new MicroScratchpadRecoveryDraft(id, content, DateTime.UtcNow);
        var preserveBackup = _preserveBackupOnNextSave.Contains(id);
        JsonFileStore.Write(
            GetDraftFilePath(id),
            draft,
            preserveBackup ? null : GetBackupFilePath(id));
        if (preserveBackup)
            _preserveBackupOnNextSave.Remove(id);
    }

    public void DeleteDraft(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A recovery ID is required.", nameof(id));

        var failures = new List<Exception>();
        DeleteFile(GetDraftFilePath(id), failures);
        DeleteFile(GetBackupFilePath(id), failures);

        try
        {
            if (Directory.Exists(_recoveryDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(
                             _recoveryDirectory,
                             $"{id:N}.json.corrupt-*"))
                {
                    DeleteFile(path, failures);
                }
            }
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            var details = string.Join(
                " ",
                failures.Select(failure => failure.Message).Distinct(StringComparer.Ordinal));
            throw new IOException(
                $"One or more recovery files for Micro Scratchpad {GetShortId(id)} could not be deleted. {details}",
                new AggregateException(failures));
        }

        _preserveBackupOnNextSave.Remove(id);
    }

    private void LoadDraft(
        Guid id,
        ICollection<MicroScratchpadRecoveryDraft> drafts,
        ICollection<MicroScratchpadRecoveryIssue> issues)
    {
        var primaryPath = GetDraftFilePath(id);
        var primary = ReadDraft(primaryPath, id);
        if (primary.Draft is not null)
        {
            drafts.Add(primary.Draft);
            return;
        }

        var backupPath = GetBackupFilePath(id);
        var backup = ReadDraft(backupPath, id);
        if (backup.Draft is not null)
        {
            var preservedPath = PreserveCorruptFile(primaryPath, primary.Status, issues);
            if (primary.Status == JsonFileReadStatus.Unavailable ||
                (primary.Status == JsonFileReadStatus.Corrupt && preservedPath is null))
            {
                _preserveBackupOnNextSave.Add(id);
            }

            drafts.Add(backup.Draft);
            issues.Add(new MicroScratchpadRecoveryIssue(
                primaryPath,
                primary.Status == JsonFileReadStatus.Missing
                    ? $"Micro Scratchpad {GetShortId(id)} was restored from its backup because the primary file was missing."
                    : $"Micro Scratchpad {GetShortId(id)} was restored from its backup because the primary file could not be loaded."));
            return;
        }

        PreserveCorruptFile(primaryPath, primary.Status, issues);
        PreserveCorruptFile(backupPath, backup.Status, issues);

        var failure = primary.Status != JsonFileReadStatus.Missing
            ? primary
            : backup;
        if (failure.Status != JsonFileReadStatus.Missing)
        {
            issues.Add(new MicroScratchpadRecoveryIssue(
                failure.Path,
                $"Micro Scratchpad {GetShortId(id)} could not be restored: {failure.Error ?? "the recovery file is invalid."}"));
        }
    }

    private static DraftReadAttempt ReadDraft(string path, Guid expectedId)
    {
        var result = JsonFileStore.Read<MicroScratchpadRecoveryDraft>(path);
        if (!result.Success)
            return new DraftReadAttempt(path, result.Status, null, result.Error?.Message);

        var draft = result.Value!;
        if (draft.Id == Guid.Empty || draft.Id != expectedId)
        {
            return new DraftReadAttempt(
                path,
                JsonFileReadStatus.Corrupt,
                null,
                "The recovery ID inside the file does not match its file name.");
        }

        return new DraftReadAttempt(path, JsonFileReadStatus.Success, draft, null);
    }

    private static string? PreserveCorruptFile(
        string path,
        JsonFileReadStatus status,
        ICollection<MicroScratchpadRecoveryIssue> issues)
    {
        if (status != JsonFileReadStatus.Corrupt || !File.Exists(path))
            return null;

        var preservedPath =
            $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, preservedPath);
            issues.Add(new MicroScratchpadRecoveryIssue(
                path,
                $"The corrupt recovery file was preserved as '{Path.GetFileName(preservedPath)}'."));
            return preservedPath;
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new MicroScratchpadRecoveryIssue(
                path,
                $"The corrupt recovery file could not be preserved under a new name: {ex.Message}"));
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

    private static bool TryGetDraftId(string path, out Guid id)
    {
        id = Guid.Empty;
        var fileName = Path.GetFileName(path);
        string? idText = null;
        if (fileName.EndsWith(BackupFileSuffix, StringComparison.OrdinalIgnoreCase))
            idText = fileName[..^BackupFileSuffix.Length];
        else if (fileName.EndsWith(PrimaryFileSuffix, StringComparison.OrdinalIgnoreCase))
            idText = fileName[..^PrimaryFileSuffix.Length];

        return idText is not null && Guid.TryParseExact(idText, "N", out id);
    }

    private static bool IsRecoveryFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(PrimaryFileSuffix, StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(BackupFileSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private string GetDraftFilePath(Guid id) =>
        Path.Combine(_recoveryDirectory, $"{id:N}.json");

    private string GetBackupFilePath(Guid id) =>
        Path.Combine(_recoveryDirectory, $"{id:N}.json.bak");

    private static string GetShortId(Guid id) =>
        id.ToString("N")[..8].ToUpperInvariant();

    private static bool IsExpectedRecoveryException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }

    private sealed record DraftReadAttempt(
        string Path,
        JsonFileReadStatus Status,
        MicroScratchpadRecoveryDraft? Draft,
        string? Error);
}
