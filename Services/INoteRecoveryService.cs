namespace Noted.Services;

public sealed record NoteRecoveryDraft(
    string FilePath,
    string Content,
    DateTime UpdatedUtc);

public interface INoteRecoveryService
{
    NoteRecoveryDraft? LoadDraft();
    NoteRecoveryDraft? LoadDraft(string filePath);
    IReadOnlyList<NoteRecoveryDraft> LoadDrafts();
    void SaveDraft(string filePath, string content);
    void DeleteDraft(string filePath);
    void MoveDraft(string oldFilePath, string newFilePath);
}
