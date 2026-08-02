using System.IO;
using System.Text.Json;

namespace Noted.Services;

public sealed class NoteEditorSessionService : INoteEditorSessionService
{
    private readonly string _sessionFilePath;
    private readonly string _backupFilePath;
    private bool _preserveBackupOnNextSave;

    public NoteEditorSessionService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var sessionDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "session");
        _sessionFilePath = Path.Combine(sessionDirectory, "editor-workspace.json");
        _backupFilePath = $"{_sessionFilePath}.bak";
    }

    public NoteEditorSessionLoadResult Load()
    {
        var issues = new List<NoteEditorSessionIssue>();
        var primary = ReadSession(_sessionFilePath);
        if (primary.Session is not null)
            return new NoteEditorSessionLoadResult(primary.Session, issues);

        var backup = ReadSession(_backupFilePath);
        if (backup.Session is not null)
        {
            var preservedPath = PreserveCorruptFile(_sessionFilePath, primary.Status, issues);
            if (primary.Status == JsonFileReadStatus.Unavailable ||
                (primary.Status == JsonFileReadStatus.Corrupt && preservedPath is null))
            {
                _preserveBackupOnNextSave = true;
            }

            issues.Add(new NoteEditorSessionIssue(
                _sessionFilePath,
                primary.Status == JsonFileReadStatus.Missing
                    ? "The note editor session was restored from its backup because the primary file was missing."
                    : "The note editor session was restored from its backup because the primary file could not be loaded."));
            return new NoteEditorSessionLoadResult(backup.Session, issues);
        }

        PreserveCorruptFile(_sessionFilePath, primary.Status, issues);
        PreserveCorruptFile(_backupFilePath, backup.Status, issues);

        var failure = primary.Status != JsonFileReadStatus.Missing
            ? primary
            : backup;
        if (failure.Status != JsonFileReadStatus.Missing)
        {
            issues.Add(new NoteEditorSessionIssue(
                failure.Path,
                $"The note editor session could not be restored: {failure.Error ?? "the session file is invalid."}"));
        }

        return new NoteEditorSessionLoadResult(new NoteEditorSession(), issues);
    }

    public void Save(NoteEditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        JsonFileStore.Write(
            _sessionFilePath,
            session,
            _preserveBackupOnNextSave ? null : _backupFilePath);
        _preserveBackupOnNextSave = false;
    }

    private static SessionReadAttempt ReadSession(string path)
    {
        var result = JsonFileStore.Read<NoteEditorSession>(path);
        if (!result.Success)
            return new SessionReadAttempt(path, result.Status, null, result.Error?.Message);

        var session = result.Value!;
        if (session.Tabs is null)
        {
            return new SessionReadAttempt(
                path,
                JsonFileReadStatus.Corrupt,
                null,
                "The session does not contain a tab list.");
        }

        return new SessionReadAttempt(path, JsonFileReadStatus.Success, session, null);
    }

    private static string? PreserveCorruptFile(
        string path,
        JsonFileReadStatus status,
        ICollection<NoteEditorSessionIssue> issues)
    {
        if (status != JsonFileReadStatus.Corrupt || !File.Exists(path))
            return null;

        var preservedPath =
            $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, preservedPath);
            issues.Add(new NoteEditorSessionIssue(
                path,
                $"The corrupt note editor session was preserved as '{Path.GetFileName(preservedPath)}'."));
            return preservedPath;
        }
        catch (Exception ex) when (IsExpectedSessionException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new NoteEditorSessionIssue(
                path,
                $"The corrupt note editor session could not be preserved under a new name: {ex.Message}"));
            return null;
        }
    }

    private static bool IsExpectedSessionException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }

    private sealed record SessionReadAttempt(
        string Path,
        JsonFileReadStatus Status,
        NoteEditorSession? Session,
        string? Error);
}
