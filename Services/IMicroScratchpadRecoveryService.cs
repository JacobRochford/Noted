namespace Noted.Services;

public sealed record MicroScratchpadRecoveryDraft(
    Guid Id,
    string Content,
    DateTime UpdatedUtc);

public interface IMicroScratchpadRecoveryService
{
    IReadOnlyList<MicroScratchpadRecoveryDraft> LoadDrafts();
    void SaveDraft(Guid id, string content);
    void DeleteDraft(Guid id);
}
