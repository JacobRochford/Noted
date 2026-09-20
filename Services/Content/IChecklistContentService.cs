using Noted.Models;

namespace Noted.Services;

public sealed record ChecklistContentIssue(
    string FilePath,
    string Message);

public sealed record ChecklistContentLoadResult(
    IReadOnlyList<ChecklistItemState> Items,
    IReadOnlyList<ChecklistTabState> Tabs,
    IReadOnlyList<ChecklistContentIssue> Issues);

public sealed record ChecklistContentSaveResult(string? Warning);

public interface IChecklistContentService
{
    ChecklistContentLoadResult LoadItems();
    ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items);
    ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs);
}
