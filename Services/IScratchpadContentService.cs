namespace Noted.Services;

public sealed record ScratchpadContentLoadResult(
    bool Success,
    bool Exists,
    string? Content,
    string? Error,
    string? Warning);

public sealed record ScratchpadContentSaveResult(
    bool Success,
    string? Error,
    string? Warning);

public interface IScratchpadContentService
{
    ScratchpadContentLoadResult TryLoadContent();
    ScratchpadContentSaveResult TrySaveContent(string content);
}
