namespace Noted.Services;

public sealed record MicroScratchpadRecoveryDraft(
    Guid Id,
    string Content,
    DateTime UpdatedUtc);

public sealed record MicroScratchpadRecoveryIssue(
    string FilePath,
    string Message);

public sealed record MicroScratchpadRecoveryLoadResult(
    MicroScratchpadRecoveryDraft? Draft,
    IReadOnlyList<MicroScratchpadRecoveryIssue> Issues);

public interface IMicroScratchpadRecoveryService
{
    MicroScratchpadRecoveryLoadResult LoadDraft();
    void SaveDraft(string content);
}
