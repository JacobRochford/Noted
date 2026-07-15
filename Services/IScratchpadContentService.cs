namespace Noted.Services;

public interface IScratchpadContentService
{
    (bool Success, bool Exists, string? Content, string? Error) TryLoadContent();
    (bool Success, string? Error) TrySaveContent(string content);
}
