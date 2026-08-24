using System.IO;
using System.Text.Json;

namespace Noted.Services;

public sealed class MicroScratchpadRecoveryService : IMicroScratchpadRecoveryService
{
    private const string PrimaryFileSuffix = ".json";
    private const string BackupFileSuffix = ".json.bak";
    private static readonly Guid SingletonDraftId =
        new("A6CB4208-79C0-4C08-9D07-9CDF33F31337");
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

    public MicroScratchpadRecoveryLoadResult LoadDraft()
    {
        if (!Directory.Exists(_recoveryDirectory))
            return new MicroScratchpadRecoveryLoadResult(null, []);

        var drafts = new List<MicroScratchpadRecoveryDraft>();
        var issues = new List<MicroScratchpadRecoveryIssue>();
        LoadDraft(SingletonDraftId, drafts, issues);
        return new MicroScratchpadRecoveryLoadResult(drafts.FirstOrDefault(), issues);
    }

    public void SaveDraft(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var draft = new MicroScratchpadRecoveryDraft(
            SingletonDraftId,
            content,
            DateTime.UtcNow);
        var preserveBackup = _preserveBackupOnNextSave.Contains(SingletonDraftId);
        JsonFileStore.Write(
            GetDraftFilePath(SingletonDraftId),
            draft,
            preserveBackup ? null : GetBackupFilePath(SingletonDraftId));
        if (preserveBackup)
            _preserveBackupOnNextSave.Remove(SingletonDraftId);
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
                    ? $"Mini Pad {GetShortId(id)} was restored from its backup because the primary file was missing."
                    : $"Mini Pad {GetShortId(id)} was restored from its backup because the primary file could not be loaded."));
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
                $"Mini Pad {GetShortId(id)} could not be restored: {failure.Error ?? "the recovery file is invalid."}"));
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
