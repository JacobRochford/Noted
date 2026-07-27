using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

public sealed class NoteRecoveryService : INoteRecoveryService
{
    private readonly string _recoveryDirectory;
    private readonly string _legacyDraftFilePath;

    public NoteRecoveryService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _recoveryDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "recovery");
        _legacyDraftFilePath = Path.Combine(_recoveryDirectory, "editor-draft.json");
    }

    public NoteRecoveryDraft? LoadDraft()
    {
        return LoadDrafts().LastOrDefault();
    }

    public NoteRecoveryDraft? LoadDraft(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalizedPath = Path.GetFullPath(filePath);
        var draft = LoadDraftFile(GetDraftFilePath(normalizedPath));
        if (draft is not null && PathsEqual(draft.FilePath, normalizedPath))
            return draft;

        var legacyDraft = LoadDraftFile(_legacyDraftFilePath);
        return legacyDraft is not null && PathsEqual(legacyDraft.FilePath, normalizedPath)
            ? legacyDraft
            : null;
    }

    public IReadOnlyList<NoteRecoveryDraft> LoadDrafts()
    {
        if (!Directory.Exists(_recoveryDirectory))
            return Array.Empty<NoteRecoveryDraft>();

        try
        {
            var drafts = new Dictionary<string, NoteRecoveryDraft>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_recoveryDirectory, "*.json"))
            {
                var draft = LoadDraftFile(path);
                if (draft is not null &&
                    (!drafts.TryGetValue(draft.FilePath, out var existing) ||
                     draft.UpdatedUtc > existing.UpdatedUtc))
                {
                    drafts[draft.FilePath] = draft;
                }
            }

            return drafts.Values
                .OrderBy(draft => draft.UpdatedUtc)
                .ToList();
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return Array.Empty<NoteRecoveryDraft>();
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
        var draftFilePath = GetDraftFilePath(normalizedPath);
        var temporaryPath = draftFilePath + ".tmp";
        var draft = new NoteRecoveryDraft(normalizedPath, content, DateTime.UtcNow);

        try
        {
            var json = JsonSerializer.Serialize(draft);
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(draftFilePath))
                File.Replace(temporaryPath, draftFilePath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, draftFilePath);

            var legacyDraft = LoadDraftFile(_legacyDraftFilePath);
            if (legacyDraft is not null && PathsEqual(legacyDraft.FilePath, normalizedPath))
                TryDeleteFile(_legacyDraftFilePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public void DeleteDraft(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        TryDeleteFile(GetDraftFilePath(normalizedPath));

        var legacyDraft = LoadDraftFile(_legacyDraftFilePath);
        if (legacyDraft is not null && PathsEqual(legacyDraft.FilePath, normalizedPath))
            TryDeleteFile(_legacyDraftFilePath);
    }

    public void MoveDraft(string oldFilePath, string newFilePath)
    {
        var draft = LoadDraft(oldFilePath);
        if (draft is null || !PathsEqual(draft.FilePath, oldFilePath))
            return;

        SaveDraft(newFilePath, draft.Content);
        DeleteDraft(oldFilePath);
    }

    private NoteRecoveryDraft? LoadDraftFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
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

    private string GetDraftFilePath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return Path.Combine(_recoveryDirectory, $"{hash}.json");
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
