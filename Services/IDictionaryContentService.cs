using Noted.Models;

namespace Noted.Services;

public sealed record DictionaryContentIssue(
    string FilePath,
    string Message);

public sealed record DictionaryContentLoadResult(
    IReadOnlyList<DictionaryItemState> Items,
    IReadOnlyList<DictionaryContentIssue> Issues);

public sealed record DictionaryContentSaveResult(string? Warning);

public interface IDictionaryContentService
{
    DictionaryContentLoadResult LoadItems();
    DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items);
}
