using Noted.Models;

namespace Noted.Services;

public sealed record JotsContentIssue(
    string FilePath,
    string Message);

public sealed record JotsContentLoadResult(
    IReadOnlyList<JotItemState> Items,
    IReadOnlyList<JotsContentIssue> Issues);

public sealed record JotsContentSaveResult(string? Warning);

public interface IJotsContentService
{
    JotsContentLoadResult LoadItems();
    JotsContentSaveResult SaveItems(IReadOnlyList<JotItemState> items);
}
