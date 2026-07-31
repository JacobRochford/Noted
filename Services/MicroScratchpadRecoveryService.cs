using System.IO;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

public sealed class MicroScratchpadRecoveryService : IMicroScratchpadRecoveryService
{
    private readonly string _recoveryDirectory;

    public MicroScratchpadRecoveryService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _recoveryDirectory = Path.Combine(
            Path.GetFullPath(storageDirectory),
            "recovery",
            "micro-scratchpads");
    }

    public IReadOnlyList<MicroScratchpadRecoveryDraft> LoadDrafts()
    {
        if (!Directory.Exists(_recoveryDirectory))
            return [];

        try
        {
            return Directory
                .EnumerateFiles(_recoveryDirectory, "*.json")
                .Select(LoadDraft)
                .Where(draft => draft is not null)
                .Cast<MicroScratchpadRecoveryDraft>()
                .OrderBy(draft => draft.UpdatedUtc)
                .ToList();
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return [];
        }
    }

    public void SaveDraft(Guid id, string content)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A recovery ID is required.", nameof(id));

        ArgumentNullException.ThrowIfNull(content);

        var draft = new MicroScratchpadRecoveryDraft(id, content, DateTime.UtcNow);
        var json = JsonSerializer.Serialize(draft);
        FileWriter.WriteAllText(GetDraftFilePath(id), json);
    }

    public void DeleteDraft(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A recovery ID is required.", nameof(id));

        var path = GetDraftFilePath(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    private MicroScratchpadRecoveryDraft? LoadDraft(string path)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(fileName, "N", out var fileId))
                return null;

            var json = File.ReadAllText(path, Encoding.UTF8);
            var draft = JsonSerializer.Deserialize<MicroScratchpadRecoveryDraft>(json);
            if (draft is null || draft.Id == Guid.Empty || draft.Id != fileId)
                return null;

            return draft;
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    private string GetDraftFilePath(Guid id) =>
        Path.Combine(_recoveryDirectory, $"{id:N}.json");

    private static bool IsExpectedRecoveryException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }
}
