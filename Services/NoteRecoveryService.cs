using System.IO;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

public sealed class NoteRecoveryService : INoteRecoveryService
{
    private readonly string _recoveryDirectory;
    private readonly string _draftFilePath;

    public NoteRecoveryService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _recoveryDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "recovery");
        _draftFilePath = Path.Combine(_recoveryDirectory, "editor-draft.json");
    }

    public NoteRecoveryDraft? LoadDraft()
    {
        if (!File.Exists(_draftFilePath))
            return null;

        try
        {
            var json = File.ReadAllText(_draftFilePath, Encoding.UTF8);
            var draft = JsonSerializer.Deserialize<NoteRecoveryDraft>(json);
            if (draft is null ||
                string.IsNullOrWhiteSpace(draft.FilePath) ||
                !NoteFileExtensions.IsSupported(draft.FilePath))
            {
                return null;
            }

            return draft with { FilePath = Path.GetFullPath(draft.FilePath) };
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    public void SaveDraft(string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var normalizedPath = Path.GetFullPath(filePath);
        if (!NoteFileExtensions.IsSupported(normalizedPath))
            throw new ArgumentException("Unsupported note file type.", nameof(filePath));

        Directory.CreateDirectory(_recoveryDirectory);
        var temporaryPath = _draftFilePath + ".tmp";
        var draft = new NoteRecoveryDraft(normalizedPath, content, DateTime.UtcNow);

        try
        {
            var json = JsonSerializer.Serialize(draft);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_draftFilePath))
                File.Replace(temporaryPath, _draftFilePath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _draftFilePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public void DeleteDraft(string filePath)
    {
        var draft = LoadDraft();
        if (draft is null || !PathsEqual(draft.FilePath, filePath))
            return;

        TryDeleteFile(_draftFilePath);
    }

    public void MoveDraft(string oldFilePath, string newFilePath)
    {
        var draft = LoadDraft();
        if (draft is null || !PathsEqual(draft.FilePath, oldFilePath))
            return;

        SaveDraft(newFilePath, draft.Content);
    }

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
