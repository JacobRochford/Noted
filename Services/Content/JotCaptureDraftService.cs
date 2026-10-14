using System.IO;
using System.Text.Json;
using Noted.Models;

namespace Noted.Services;

internal sealed class JotCaptureDraftService
{
    private readonly JsonCollectionFileStore<JotCaptureDraft> _fileStore;
    private readonly string _backupPath;

    internal JotCaptureDraftService(string directory)
    {
        var path = Path.Combine(directory, PersistencePaths.JotCaptureDraftFile);
        _backupPath = $"{path}.bak";
        _fileStore = new JsonCollectionFileStore<JotCaptureDraft>(path, "Quick Jot draft", ValidateDrafts);
    }

    internal JotCaptureDraft Load(out string? warning)
    {
        var result = _fileStore.Load();
        warning = result.Issues.Count == 0 ? null : string.Join(" ", result.Issues.Select(issue => issue.Message));
        var draft = result.Items?.FirstOrDefault() ?? new JotCaptureDraft();
        if (result.Items is not null && result.Issues.Count == 0 && draft.Text.Length == 0)
        {
            try
            {
                // older clears left active text in the fallback; repair it while the empty primary exists.
                var backup = JsonFileStore.Read<List<JotCaptureDraft>>(_backupPath);
                if (!backup.Success || backup.Value!.Count != 1 || backup.Value[0] != draft)
                    JsonFileStore.Write(_backupPath, new[] { draft });
            }
            catch (Exception ex) when (FileSystemErrors.IsExpected(ex))
            {
                ExceptionDiagnostics.Record(ex);
                warning = "The Quick Jot draft is empty, but its old recovery backup could not be cleared. Resolve the backup file error and restart Noted.";
            }
        }
        return draft;
    }

    internal string? Save(JotCaptureDraft draft)
    {
        ValidateDrafts([draft]);
        if (draft.Text.Length > 0)
            return _fileStore.Write([draft]);
        if (WritesBlocked)
            throw new IOException("The Quick Jot draft could not be recovered safely.");

        // clear the fallback first so replacing the primary cannot resurrect old text.
        JsonFileStore.Write(_backupPath, new[] { draft });
        return _fileStore.Write([draft], updateBackup: false);
    }

    internal bool WritesBlocked => _fileStore.WritesBlocked;

    internal static JotCaptureDraft? ValidateBackupDraft(byte[] bytes)
    {
        var drafts = JsonSerializer.Deserialize<List<JotCaptureDraft>>(bytes)
            ?? throw new JsonException("The Quick Jot draft is invalid.");
        ValidateDrafts(drafts);
        return drafts.FirstOrDefault();
    }

    private static void ValidateDrafts(IReadOnlyList<JotCaptureDraft> drafts)
    {
        if (drafts.Count > 1 || drafts.Any(draft => draft is null ||
            draft.Id == Guid.Empty || draft.Text is null))
            throw new JsonException("The Quick Jot draft is invalid.");
    }
}
