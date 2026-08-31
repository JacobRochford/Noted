using System.IO;
using System.Text.Json;

namespace Noted.Services;

public sealed class MiniPadRecoveryService : IMiniPadRecoveryService
{
    private const string PrimaryFileSuffix = ".json";
    private const string BackupFileSuffix = ".json.bak";
    private static readonly Guid MiniPadDraftId =
        new("A6CB4208-79C0-4C08-9D07-9CDF33F31337");
    private readonly string _recoveryDirectory;
    private bool _preserveBackupOnNextSave;
    private bool _writesBlocked;

    public MiniPadRecoveryService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _recoveryDirectory = Path.Combine(
            Path.GetFullPath(storageDirectory),
            "recovery",
            "micro-scratchpads");
    }

    public MiniPadRecoveryLoadResult LoadDraft()
    {
        if (!Directory.Exists(_recoveryDirectory))
            return new MiniPadRecoveryLoadResult(null, []);

        var issues = new List<MiniPadRecoveryIssue>();
        var draft = LoadSavedDraft(issues);
        return new MiniPadRecoveryLoadResult(draft, issues);
    }

    public string? SaveDraft(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_writesBlocked)
        {
            throw new IOException(
                "MiniPad recovery data cannot be saved because an existing recovery file could not be read or preserved. Resolve the reported file error and restart Noted before retrying.");
        }

        var primaryPath = GetDraftFilePath();
        var existing = ReadDraft(primaryPath).Draft;
        if (existing is not null && string.Equals(existing.Content, content, StringComparison.Ordinal))
            return null;

        var draft = new MiniPadRecoveryDraft(
            MiniPadDraftId,
            content,
            DateTime.UtcNow);
        var preserveBackup = _preserveBackupOnNextSave;
        FileWriteResult writeResult;
        try
        {
            writeResult = JsonFileStore.Write(
                primaryPath,
                draft,
                preserveBackup ? null : GetBackupFilePath());
        }
        catch (FileVerificationException)
        {
            _writesBlocked = true;
            throw;
        }

        if (preserveBackup)
            _preserveBackupOnNextSave = false;
        return writeResult.Warning;
    }

    private MiniPadRecoveryDraft? LoadSavedDraft(
        ICollection<MiniPadRecoveryIssue> issues)
    {
        var primaryPath = GetDraftFilePath();
        var primary = ReadDraft(primaryPath);
        if (primary.Draft is not null)
            return primary.Draft;

        var backupPath = GetBackupFilePath();
        var backup = ReadDraft(backupPath);
        if (backup.Draft is not null)
        {
            var preservedPath = PreserveCorruptFile(primaryPath, primary.Status, issues);
            if (primary.Status == JsonFileReadStatus.Unavailable ||
                (primary.Status == JsonFileReadStatus.Corrupt && preservedPath is null))
            {
                _preserveBackupOnNextSave = true;
            }

            issues.Add(new MiniPadRecoveryIssue(
                primaryPath,
                primary.Status == JsonFileReadStatus.Missing
                    ? "MiniPad was restored from its backup because the primary file was missing."
                    : "MiniPad was restored from its backup because the primary file could not be loaded."));
            return backup.Draft;
        }

        var primaryPreserved = PreserveCorruptFile(primaryPath, primary.Status, issues);
        var backupPreserved = PreserveCorruptFile(backupPath, backup.Status, issues);
        _writesBlocked = primary.Status == JsonFileReadStatus.Unavailable ||
            backup.Status == JsonFileReadStatus.Unavailable ||
            (primary.Status == JsonFileReadStatus.Corrupt && primaryPreserved is null) ||
            (backup.Status == JsonFileReadStatus.Corrupt && backupPreserved is null);

        var failure = primary.Status != JsonFileReadStatus.Missing
            ? primary
            : backup;
        if (failure.Status != JsonFileReadStatus.Missing)
        {
            issues.Add(new MiniPadRecoveryIssue(
                failure.Path,
                $"MiniPad could not be restored: {failure.Error ?? "the recovery file is invalid."}"));
        }

        return null;
    }

    private static DraftReadResult ReadDraft(string path)
    {
        var result = JsonFileStore.Read<MiniPadRecoveryDraft>(path);
        if (!result.Success)
            return new DraftReadResult(path, result.Status, null, result.Error?.Message);

        var draft = result.Value!;
        if (draft.Id == Guid.Empty || draft.Id != MiniPadDraftId)
        {
            return new DraftReadResult(
                path,
                JsonFileReadStatus.Corrupt,
                null,
                "The recovery ID inside the file does not match its file name.");
        }

        return new DraftReadResult(path, JsonFileReadStatus.Success, draft, null);
    }

    private static string? PreserveCorruptFile(
        string path,
        JsonFileReadStatus status,
        ICollection<MiniPadRecoveryIssue> issues)
    {
        if (status != JsonFileReadStatus.Corrupt || !File.Exists(path))
            return null;

        var preservedPath =
            $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, preservedPath);
            issues.Add(new MiniPadRecoveryIssue(
                path,
                $"The corrupt recovery file was preserved as '{Path.GetFileName(preservedPath)}'."));
            return preservedPath;
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new MiniPadRecoveryIssue(
                path,
                $"The corrupt recovery file could not be preserved under a new name: {ex.Message}"));
            return null;
        }
    }

    private string GetDraftFilePath() =>
        Path.Combine(_recoveryDirectory, $"{MiniPadDraftId:N}{PrimaryFileSuffix}");

    private string GetBackupFilePath() =>
        Path.Combine(_recoveryDirectory, $"{MiniPadDraftId:N}{BackupFileSuffix}");

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
        MiniPadRecoveryDraft? Draft,
        string? Error);
}
