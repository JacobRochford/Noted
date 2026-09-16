using System.IO;
using Noted.Models;

namespace Noted.Services;

public sealed class ChecklistContentService : IChecklistContentService
{
    private readonly JsonCollectionFileStore<ChecklistItemState> _itemStore;
    private readonly JsonCollectionFileStore<ChecklistTabState> _tabStore;

    public ChecklistContentService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var normalizedStorageDirectory = Path.GetFullPath(storageDirectory);
        _itemStore = new JsonCollectionFileStore<ChecklistItemState>(
            Path.Combine(normalizedStorageDirectory, "checklist.json"),
            "Checklist content");
        _tabStore = new JsonCollectionFileStore<ChecklistTabState>(
            Path.Combine(normalizedStorageDirectory, "checklist-tabs.json"),
            "Checklist tabs");
    }

    public ChecklistContentLoadResult LoadItems()
    {
        var itemResult = _itemStore.Load();
        var tabResult = _tabStore.Load();
        var issues = itemResult.Issues
            .Select(issue => new ChecklistContentIssue(issue.FilePath, issue.Message))
            .Concat(tabResult.Issues.Select(
                issue => new ChecklistContentIssue(issue.FilePath, issue.Message)))
            .ToList();

        return new ChecklistContentLoadResult(
            itemResult.Items ?? [],
            tabResult.Items ?? [],
            issues);
    }

    public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new ChecklistContentSaveResult(_itemStore.Write(items));
    }

    public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        return new ChecklistContentSaveResult(_tabStore.Write(tabs));
    }
}
