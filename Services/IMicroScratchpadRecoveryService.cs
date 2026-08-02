namespace Noted.Services;

public sealed record MicroScratchpadRecoveryDraft(
    Guid Id,
    string Content,
    DateTime UpdatedUtc);

public sealed record MicroScratchpadRecoveryIssue(
    string FilePath,
    string Message);

public sealed record MicroScratchpadRecoveryLoadResult(
    IReadOnlyList<MicroScratchpadRecoveryDraft> Drafts,
    IReadOnlyList<MicroScratchpadRecoveryIssue> Issues);

public interface IMicroScratchpadRecoveryService
{
    MicroScratchpadRecoveryLoadResult LoadDrafts();
    void SaveDraft(Guid id, string content);
    void DeleteDraft(Guid id);
}
