using System.IO;
using Noted.Models;

namespace Noted.Services;

public sealed class ChecklistContentService : IChecklistContentService
{
    private readonly IAppSettingsService _settingsService;
    private readonly JsonCollectionFileStore<ChecklistItemState> _itemStore;
    private readonly JsonCollectionFileStore<ChecklistTabState> _tabStore;
    private bool _legacyReadBlocksWrites;

    public ChecklistContentService(
        string storageDirectory,
        IAppSettingsService settingsService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
        _itemStore = new JsonCollectionFileStore<ChecklistItemState>(
            Path.Combine(Path.GetFullPath(storageDirectory), "checklist.json"),
            "Checklist content");
        _tabStore = new JsonCollectionFileStore<ChecklistTabState>(
            Path.Combine(Path.GetFullPath(storageDirectory), "checklist-tabs.json"),
            "Checklist tabs");
    }

    public ChecklistContentLoadResult LoadItems()
    {
        var fileResult = _itemStore.Load();
        var tabResult = _tabStore.Load();
        var issues = fileResult.Issues
            .Select(issue => new ChecklistContentIssue(issue.FilePath, issue.Message))
            .Concat(tabResult.Issues.Select(
                issue => new ChecklistContentIssue(issue.FilePath, issue.Message)))
            .ToList();
        var tabs = tabResult.Items ?? [];

        if (!TryLoadLegacyItems(out var legacyItems, out var legacyError))
        {
            issues.Add(new ChecklistContentIssue(_itemStore.PrimaryFilePath, legacyError!));
            if (fileResult.Items is null)
                _legacyReadBlocksWrites = true;
            return new ChecklistContentLoadResult(fileResult.Items ?? [], tabs, issues);
        }

        if (fileResult.Items is not null)
        {
            TryClearLegacyItems(legacyItems, issues);
            return new ChecklistContentLoadResult(fileResult.Items, tabs, issues);
        }

        if (legacyItems is null)
            return new ChecklistContentLoadResult([], tabs, issues);

        try
        {
            _itemStore.WriteAndVerify(legacyItems);
            TryClearLegacyItems(legacyItems, issues);
        }
        catch (Exception ex) when (IsExpectedContentException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new ChecklistContentIssue(
                _itemStore.PrimaryFilePath,
                $"Checklist content could not be migrated out of settings: {ex.Message}"));
        }

        return new ChecklistContentLoadResult(legacyItems, tabs, issues);
    }

    public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_legacyReadBlocksWrites)
        {
            throw new IOException(
                "Checklist content cannot be saved because the legacy settings copy could not be checked. Resolve the reported settings error and restart Noted before retrying.");
        }

        _itemStore.Write(items);
        try
        {
            var legacyItems = _settingsService.LoadLegacyChecklistItems();
            if (legacyItems is not null)
                _settingsService.ClearLegacyChecklistItems();
            return new ChecklistContentSaveResult(null);
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return new ChecklistContentSaveResult(
                $"Checklist content was saved, but its old settings copy could not be removed: {ex.Message}");
        }
    }

    public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        _tabStore.Write(tabs);
        return new ChecklistContentSaveResult(null);
    }

    private bool TryLoadLegacyItems(
        out IReadOnlyList<ChecklistItemState>? items,
        out string? error)
    {
        try
        {
            items = _settingsService.LoadLegacyChecklistItems();
            error = null;
            return true;
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            items = null;
            error = $"The legacy Checklist settings copy could not be read: {ex.Message}";
            return false;
        }
    }

    private void TryClearLegacyItems(
        IReadOnlyList<ChecklistItemState>? legacyItems,
        ICollection<ChecklistContentIssue> issues)
    {
        if (legacyItems is null)
            return;

        try
        {
            _settingsService.ClearLegacyChecklistItems();
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new ChecklistContentIssue(
                _itemStore.PrimaryFilePath,
                $"Checklist content is stored in its dedicated file, but the old settings copy could not be removed: {ex.Message}"));
        }
    }

    private static bool IsExpectedContentException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            SettingsPersistenceException or
            ArgumentException or
            NotSupportedException;
    }
}
