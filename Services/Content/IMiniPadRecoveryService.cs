namespace Noted.Services;

public sealed record MiniPadRecoveryDraft(
    Guid Id,
    string Content,
    DateTime UpdatedUtc);

public sealed record MiniPadRecoveryIssue(
    string FilePath,
    string Message);

public sealed record MiniPadRecoveryLoadResult(
    MiniPadRecoveryDraft? Draft,
    IReadOnlyList<MiniPadRecoveryIssue> Issues);

public interface IMiniPadRecoveryService
{
    MiniPadRecoveryLoadResult LoadDraft();
    string? SaveDraft(string content);
}
