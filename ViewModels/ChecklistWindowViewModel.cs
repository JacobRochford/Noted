using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public enum ChecklistSortMode  { Manual, Priority, DueDate, Alphabetical }

public sealed class ChecklistWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan s_saveQuietPeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan s_saveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly IChecklistContentService _contentService;
    private readonly SaveScheduler<IReadOnlyList<ChecklistItemState>> _saveScheduler;
    private readonly Action<Action> _uiThreadInvoke;
    private readonly ChecklistTabs _checklistTabs;
    private bool _ghostModeEnabled;
    private string _searchQuery = "";
    private ChecklistSortMode _sortMode = ChecklistSortMode.Manual;
    private bool _isSearchVisible;
    private bool _isRefreshing;
    private bool _isBulkUpdating;
    private string? _persistenceError;
    private string? _itemSaveError;
    private string? _tabSaveError;
    private string? _itemSaveWarning;
    private string? _tabSaveWarning;
    private string? _loadNotice;

    // Source collection in saved order
    public ObservableCollection<ChecklistItem> Items { get; } = new();

    // Filtered + sorted view shown to the ListBox
    public ObservableCollection<ChecklistItem> FilteredItems { get; } = new();
    public ObservableCollection<ChecklistTab> Tabs => _checklistTabs.All;
    public ObservableCollection<ChecklistTab> VisibleTabs => _checklistTabs.Visible;

    // Window state

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set { if (_ghostModeEnabled != value) { _ghostModeEnabled = value; OnPropertyChanged(); } }
    }

    // Filter / search / sort

    public string SearchQuery
    {
        get => _searchQuery;
        set { if (_searchQuery != value) { _searchQuery = value; OnPropertyChanged(); RefreshFilteredItems(); } }
    }

    public ChecklistTab? SelectedTab
    {
        get => _checklistTabs.Selected;
        private set => _checklistTabs.Select(value);
    }

    public bool HasSelectedTab => SelectedTab is not null;

    public bool CanAddItem => SelectedTab is not null &&
        (SelectedTab.Kind != ChecklistTabKind.Done ||
         VisibleTabs.Any(tab => tab.Kind != ChecklistTabKind.Done));

    public IEnumerable<ChecklistTab> CustomTabs =>
        _checklistTabs.Custom;

    public ChecklistSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode == value) return;
            _sortMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanManuallyReorder));
            RefreshFilteredItems();
        }
    }

    public bool CanManuallyReorder => SortMode == ChecklistSortMode.Manual;

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        set { if (_isSearchVisible != value) { _isSearchVisible = value; OnPropertyChanged(); } }
    }

    public string? PersistenceError
    {
        get => _persistenceError;
        private set
        {
            if (_persistenceError == value) return;
            _persistenceError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPersistenceError));
        }
    }

    public bool HasPersistenceError => !string.IsNullOrWhiteSpace(PersistenceError);
    internal bool RecoveryIssuesFoundThisRun { get; }

    // Progress / stats

    public int TotalCount     => Items.Count;
    public int CompletedCount => Items.Count(i => i.IsChecked);
    public int RemainingCount => Items.Count(i => !i.IsChecked);
    public double ProgressPercent => TotalCount > 0 ? (double)CompletedCount / TotalCount * 100 : 0;
    public string ProgressText => TotalCount == 0 ? "" : $"{CompletedCount}/{TotalCount}";
    public bool HasCompleted  => Items.Any(i => i.IsChecked);

    public string FooterText => RemainingCount switch
    {
        _ when TotalCount == 0 => "No items — click + Add",
        0 => "All done! 🎉",
        1 => "1 item remaining",
        _ => $"{RemainingCount} items remaining"
    };

    // Constructor

    public ChecklistWindowViewModel(
        IChecklistContentService contentService,
        Dispatcher dispatcher,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(uiThreadInvoke);
        _contentService = contentService;
        _saveScheduler = new SaveScheduler<IReadOnlyList<ChecklistItemState>>(
            dispatcher,
            s_saveQuietPeriod,
            s_saveMaximumDelay,
            SaveItemsSnapshot);
        _uiThreadInvoke = uiThreadInvoke;
        _ghostModeEnabled = ghostModeEnabled;

        var loadResult = _contentService.LoadItems();
        RecoveryIssuesFoundThisRun = loadResult.Issues.Count > 0;
        SetLoadIssues(loadResult.Issues);
        _checklistTabs = new ChecklistTabs(loadResult.Tabs, TrySaveTabsSnapshot);
        _checklistTabs.SelectionChanged += ChecklistTabs_SelectionChanged;
        var customTabsById = CustomTabs.ToDictionary(tab => tab.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var d in loadResult.Items)
        {
            customTabsById.TryGetValue(d.TabId ?? "", out var assignedTab);
            var item = new ChecklistItem
            {
                Text      = d.Text ?? "",
                IsChecked = d.IsChecked,
                Priority  = d.Priority,
                TabId     = assignedTab?.Id,
                DueDate   = d.DueDate,
                Notes     = d.Notes ?? "",
                CreatedAt = d.CreatedAt == default ? DateTime.Now : d.CreatedAt
            };
            item.PropertyChanged += Item_PropertyChanged;
            Items.Add(item);
        }

        Items.CollectionChanged += Items_CollectionChanged;
        RefreshFilteredItems();
    }

    // Tabs

    public void SelectTab(ChecklistTab? tab) =>
        _checklistTabs.Select(tab);

    public ChecklistTab? FindTab(string id) =>
        _checklistTabs.Find(id);

    public bool SetDefaultTabVisibility(string id, bool isVisible)
    {
        var saved = _checklistTabs.SetDefaultVisibility(id, isVisible);
        if (saved)
            OnPropertyChanged(nameof(CanAddItem));
        return saved;
    }

    public bool TryCreateTab(string name, out ChecklistTab? createdTab) =>
        _checklistTabs.TryCreate(name, out createdTab);

    public bool TryRenameTab(ChecklistTab tab, string name) =>
        _checklistTabs.TryRename(tab, name);

    public bool TryDeleteTab(ChecklistTab tab)
    {
        if (!_checklistTabs.TryDelete(tab))
            return false;

        RunBulkUpdate(() =>
        {
            foreach (var item in Items.Where(item => item.TabId == tab.Id))
                item.TabId = null;
        });
        OnPropertyChanged(nameof(CanAddItem));
        return true;
    }

    public bool IsTabNameAvailable(string name, ChecklistTab? existingTab) =>
        _checklistTabs.IsNameAvailable(name, existingTab);

    public void MoveItemToTab(ChecklistItem item, string? tabId)
    {
        if (!Items.Contains(item)) return;
        var validTabId = tabId is not null &&
                         CustomTabs.Any(tab => tab.Id == tabId)
            ? tabId
            : null;
        item.TabId = validTabId;
    }

    private void ChecklistTabs_SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(HasSelectedTab));
        OnPropertyChanged(nameof(CanAddItem));
        RefreshFilteredItems();
    }

    // Collection / item change handlers

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ChecklistItem item in e.NewItems)
                item.PropertyChanged += Item_PropertyChanged;

        if (e.OldItems != null)
            foreach (ChecklistItem item in e.OldItems)
                item.PropertyChanged -= Item_PropertyChanged;

        if (_isBulkUpdating) return;

        NotifyProgress();
        SaveItems();
        RefreshFilteredItems();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRefreshing ||
            _isBulkUpdating ||
            e.PropertyName == nameof(ChecklistItem.IsExpanded)) return; // UI-only, don't save

        NotifyProgress();
        SaveItems();
        RefreshFilteredItems();
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(FooterText));
    }

    // Filtering / sorting

    public void RefreshFilteredItems()
    {
         _isRefreshing = true;
        try {
            var source = Items.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchQuery))
                source = source.Where(i =>
                    i.Text.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                    i.Notes.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

            source = SelectedTab?.Kind switch
            {
                ChecklistTabKind.All => source.Where(item => !item.IsChecked),
                ChecklistTabKind.Urgent => source.Where(
                    item => !item.IsChecked && item.Priority == ChecklistPriority.High),
                ChecklistTabKind.Done => source.Where(item => item.IsChecked),
                ChecklistTabKind.Custom => source.Where(
                    item => !item.IsChecked && item.TabId == SelectedTab.Id),
                _ => Enumerable.Empty<ChecklistItem>()
            };

            source = SortMode switch
            {
                ChecklistSortMode.Priority    => source.OrderByDescending(i => (int)i.Priority),
                ChecklistSortMode.DueDate     => source.OrderBy(i => i.DueDate.HasValue ? 0 : 1)
                                                    .ThenBy(i => i.DueDate ?? DateTime.MaxValue),
                ChecklistSortMode.Alphabetical => source.OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase),
                _                              => source
            };

            var result = source.ToList();

            // In-place sync to minimize ListBox re-renders
            for (int i = 0; i < result.Count; i++)
            {
                if (i < FilteredItems.Count)
                {
                    if (!ReferenceEquals(FilteredItems[i], result[i]))
                        FilteredItems[i] = result[i];
                }
                else
                {
                    FilteredItems.Add(result[i]);
                }
            }
            while (FilteredItems.Count > result.Count)
                FilteredItems.RemoveAt(FilteredItems.Count - 1);
        } finally { _isRefreshing = false; }
    }

    // CRUD

    public ChecklistItem? AddItem(ChecklistItem? anchor = null)
    {
        ChecklistItem? item = null;
        _uiThreadInvoke(() =>
        {
            var creationTab = GetCreationTab();
            if (creationTab is null) return;

            PrepareViewForNewItem(creationTab);
            item = new ChecklistItem
            {
                CreatedAt = DateTime.Now,
                Priority = creationTab.Kind == ChecklistTabKind.Urgent
                    ? ChecklistPriority.High
                    : ChecklistPriority.None,
                TabId = creationTab.Kind == ChecklistTabKind.Custom
                    ? creationTab.Id
                    : null
            };

            var anchorIndex = SortMode == ChecklistSortMode.Manual && anchor is not null
                ? Items.IndexOf(anchor)
                : -1;
            if (anchorIndex >= 0)
                Items.Insert(anchorIndex + 1, item!);
            else
                Items.Add(item!);
        });
        return item;
    }

    public ChecklistItem? InsertItemAfter(ChecklistItem anchor)
    {
        return AddItem(anchor);
    }

    private ChecklistTab? GetCreationTab()
    {
        if (SelectedTab?.Kind != ChecklistTabKind.Done)
            return SelectedTab;

        return VisibleTabs.FirstOrDefault(tab => tab.Kind == ChecklistTabKind.All) ??
               VisibleTabs.FirstOrDefault(tab => tab.Kind == ChecklistTabKind.Custom) ??
               VisibleTabs.FirstOrDefault(tab => tab.Kind == ChecklistTabKind.Urgent);
    }

    private void PrepareViewForNewItem(ChecklistTab creationTab)
    {
        var refreshNeeded = false;
        if (!ReferenceEquals(SelectedTab, creationTab))
        {
            SelectedTab = creationTab;
            refreshNeeded = true;
        }

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            _searchQuery = "";
            OnPropertyChanged(nameof(SearchQuery));
            refreshNeeded = true;
        }

        if (refreshNeeded)
            RefreshFilteredItems();
    }

    public void RemoveItem(ChecklistItem item) => _uiThreadInvoke(() => Items.Remove(item));

    public void DuplicateItem(ChecklistItem item)
    {
        var idx = Items.IndexOf(item);
        var dup = new ChecklistItem
        {
            Text      = item.Text,
            Priority  = item.Priority,
            TabId     = item.TabId,
            DueDate   = item.DueDate,
            Notes     = item.Notes,
            CreatedAt = DateTime.Now
        };
        Items.Insert(idx >= 0 ? idx + 1 : Items.Count, dup);
    }

    // Bulk operations

    public void ClearCompleted()
    {
        _uiThreadInvoke(() =>
        {
            var completedItems = Items.Where(item => item.IsChecked).ToList();
            if (completedItems.Count == 0) return;

            RunBulkUpdate(() =>
            {
                foreach (var item in completedItems)
                    Items.Remove(item);
            });
        });
    }

    public void CheckAll() => SetAllChecked(true);
    public void UncheckAll() => SetAllChecked(false);

    private void SetAllChecked(bool isChecked)
    {
        _uiThreadInvoke(() =>
        {
            var changedItems = FilteredItems
                .Where(item => item.IsChecked != isChecked)
                .ToList();
            if (changedItems.Count == 0) return;

            RunBulkUpdate(() =>
            {
                foreach (var item in changedItems)
                    item.IsChecked = isChecked;
            });
        });
    }

    private void RunBulkUpdate(Action update)
    {
        _isBulkUpdating = true;
        try
        {
            update();
        }
        finally
        {
            _isBulkUpdating = false;
        }

        NotifyProgress();
        SaveItems();
        RefreshFilteredItems();
    }

    // Reorder

    public void MoveItemUp(ChecklistItem item)
    {
        if (!CanManuallyReorder) return;
        var visibleIndex = FilteredItems.IndexOf(item);
        if (visibleIndex > 0)
            MoveItem(item, FilteredItems[visibleIndex - 1]);
    }

    public void MoveItemDown(ChecklistItem item)
    {
        if (!CanManuallyReorder) return;
        var visibleIndex = FilteredItems.IndexOf(item);
        if (visibleIndex >= 0 && visibleIndex < FilteredItems.Count - 1)
            MoveItem(item, FilteredItems[visibleIndex + 1]);
    }

    public void MoveToTop(ChecklistItem item)
    {
        if (!CanManuallyReorder || FilteredItems.Count == 0) return;
        var firstVisible = FilteredItems[0];
        if (!ReferenceEquals(item, firstVisible))
            MoveItem(item, firstVisible);
    }

    public void MoveToBottom(ChecklistItem item)
    {
        if (!CanManuallyReorder || FilteredItems.Count == 0) return;
        var lastVisible = FilteredItems[^1];
        if (!ReferenceEquals(item, lastVisible))
            MoveItem(item, lastVisible);
    }

    public void MoveItem(ChecklistItem source, ChecklistItem target)
    {
        if (!CanManuallyReorder) return;
        var si = Items.IndexOf(source);
        var ti = Items.IndexOf(target);
        if (si >= 0 && ti >= 0 && si != ti)
            Items.Move(si, ti);
    }

    public void MoveItem(ChecklistItem source, ChecklistItem target, bool placeAfter)
    {
        if (!CanManuallyReorder) return;
        var sourceIndex = Items.IndexOf(source);
        var targetIndex = Items.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;

        var newIndex = placeAfter
            ? sourceIndex < targetIndex ? targetIndex : targetIndex + 1
            : sourceIndex < targetIndex ? targetIndex - 1 : targetIndex;
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);

        if (newIndex != sourceIndex)
            Items.Move(sourceIndex, newIndex);
    }

    // Persistence

    public bool TryFlushPendingItems(out string? error)
    {
        return _saveScheduler.TryFlush(out error);
    }

    public void Dispose()
    {
        _checklistTabs.SelectionChanged -= ChecklistTabs_SelectionChanged;
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in Items)
            item.PropertyChanged -= Item_PropertyChanged;
        _saveScheduler.Dispose();
    }

    private void SaveItems()
    {
        _saveScheduler.Schedule(CreateItemsSnapshot());
    }

    private IReadOnlyList<ChecklistItemState> CreateItemsSnapshot()
    {
        return Items.Select(i => new ChecklistItemState
        {
            Text      = i.Text,
            IsChecked = i.IsChecked,
            Priority  = i.Priority,
            TabId     = i.TabId,
            DueDate   = i.DueDate,
            Notes     = i.Notes,
            CreatedAt = i.CreatedAt
        }).ToList();
    }

    private bool TrySaveTabsSnapshot(IReadOnlyList<ChecklistTabState> tabs)
    {
        try
        {
            var saveResult = _contentService.SaveTabs(tabs);
            _tabSaveError = null;
            _tabSaveWarning = saveResult.Warning;
            _loadNotice = null;
            RefreshPersistenceError();
            return true;
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            _tabSaveError = "Checklist tabs could not be saved.";
            _tabSaveWarning = null;
            RefreshPersistenceError();
            return false;
        }
    }

    private PersistenceSaveResult SaveItemsSnapshot(IReadOnlyList<ChecklistItemState> items)
    {
        try
        {
            var saveResult = _contentService.SaveItems(items);
            _itemSaveError = null;
            _itemSaveWarning = saveResult.Warning;
            _loadNotice = null;
            RefreshPersistenceError();
            return PersistenceSaveResult.Succeeded();
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            _itemSaveError = "Checklist content could not be saved.";
            _itemSaveWarning = null;
            RefreshPersistenceError();
            return PersistenceSaveResult.Failed(_itemSaveError);
        }
    }

    private void SetLoadIssues(IReadOnlyList<ChecklistContentIssue> issues)
    {
        _loadNotice = issues.Count == 0
            ? null
            : string.Join(
                " ",
                issues.Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"));
        RefreshPersistenceError();
    }

    // Failures precede warnings; item saves take precedence within each category.
    private void RefreshPersistenceError()
        => PersistenceError = _itemSaveError
            ?? _tabSaveError
            ?? _itemSaveWarning
            ?? _tabSaveWarning
            ?? _loadNotice;

    private static bool IsExpectedPersistenceException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);

}
