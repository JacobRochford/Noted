namespace Noted.Services;

public sealed record NoteRecoveryDraft(
    string FilePath,
    string Content,
    DateTime UpdatedUtc);

public sealed record NoteRecoveryIssue(
    string FilePath,
    string Message);

public sealed record NoteRecoveryDraftLoadResult(
    NoteRecoveryDraft? Draft,
    IReadOnlyList<NoteRecoveryIssue> Issues);

public sealed record NoteRecoveryLoadResult(
    IReadOnlyList<NoteRecoveryDraft> Drafts,
    IReadOnlyList<NoteRecoveryIssue> Issues);

public interface INoteRecoveryService
{
    NoteRecoveryDraftLoadResult LoadDraft(string filePath);
    NoteRecoveryLoadResult LoadDrafts();
    string? SaveDraft(string filePath, string content);
    void DeleteDraft(string filePath);
}
