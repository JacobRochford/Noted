using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public enum ChecklistSortMode  { Manual, Priority, DueDate, Alphabetical }

public sealed class ChecklistWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan SaveQuietPeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SaveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly IChecklistContentService _contentService;
    private readonly SaveScheduler<IReadOnlyList<ChecklistItemState>> _saveScheduler;
    private readonly Action<Action> _uiThreadInvoke;
    private bool _ghostModeEnabled;
    private string _searchQuery = "";
    private ChecklistTab? _selectedTab;
    private int _nextCustomTabOrder = 100;
    private ChecklistSortMode _sortMode = ChecklistSortMode.Manual;
    private bool _isSearchVisible;
    private bool _isRefreshing;
    private bool _isBulkUpdating;
    private string? _persistenceError;

    // Source collection in saved order
    public ObservableCollection<ChecklistItem> Items { get; } = new();

    // Filtered + sorted view shown to the ListBox
    public ObservableCollection<ChecklistItem> FilteredItems { get; } = new();
    public ObservableCollection<ChecklistTab> Tabs { get; } = new();
    public ObservableCollection<ChecklistTab> VisibleTabs { get; } = new();

    // --- Window state ---

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set { if (_ghostModeEnabled != value) { _ghostModeEnabled = value; OnPropertyChanged(); } }
    }

    // --- Filter / search / sort ---

    public string SearchQuery
    {
        get => _searchQuery;
        set { if (_searchQuery != value) { _searchQuery = value; OnPropertyChanged(); RefreshFilteredItems(); } }
    }

    public ChecklistTab? SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            if (_selectedTab is not null) _selectedTab.IsSelected = false;
            _selectedTab = value;
            if (_selectedTab is not null) _selectedTab.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTab));
            OnPropertyChanged(nameof(CanAddItem));
            RefreshFilteredItems();
        }
    }

    public bool HasSelectedTab => SelectedTab is not null;

    public bool CanAddItem => SelectedTab is not null &&
        (SelectedTab.Kind != ChecklistTabKind.Done ||
         VisibleTabs.Any(tab => tab.Kind != ChecklistTabKind.Done));

    public IEnumerable<ChecklistTab> CustomTabs =>
        Tabs.Where(tab => tab.Kind == ChecklistTabKind.Custom);

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

    // --- Progress / stats ---

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

    // --- Constructor ---

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
            SaveQuietPeriod,
            SaveMaximumDelay,
            SaveItemsSnapshot);
        _uiThreadInvoke = uiThreadInvoke;
        _ghostModeEnabled = ghostModeEnabled;

        var loadResult = _contentService.LoadItems();
        SetLoadIssues(loadResult.Issues);
        InitializeTabs(loadResult.Tabs);
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

    // --- Tabs ---

    private void InitializeTabs(IReadOnlyList<ChecklistTabState> states)
    {
        AddSystemTab(
            states.FirstOrDefault(state => state.Kind == ChecklistTabKind.All),
            ChecklistTab.AllId,
            "All",
            ChecklistTabKind.All,
            0);
        AddSystemTab(
            states.FirstOrDefault(state => state.Kind == ChecklistTabKind.Urgent),
            ChecklistTab.UrgentId,
            "Urgent",
            ChecklistTabKind.Urgent,
            1);

        var usedIds = new HashSet<string>(
            Tabs.Select(tab => tab.Id),
            StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(
            Tabs.Select(tab => tab.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var state in states
                     .Where(state => state.Kind == ChecklistTabKind.Custom)
                     .OrderBy(state => state.Order))
        {
            var id = state.Id?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!usedIds.Add(id));
            }

            var name = MakeUniqueTabName(NormalizeTabName(state.Name), usedNames);

            var order = Math.Max(100, state.Order);
            Tabs.Add(new ChecklistTab(
                id,
                name,
                ChecklistTabKind.Custom,
                isVisible: true,
                order));
            _nextCustomTabOrder = Math.Max(_nextCustomTabOrder, order + 1);
        }

        AddSystemTab(
            states.FirstOrDefault(state => state.Kind == ChecklistTabKind.Done),
            ChecklistTab.DoneId,
            "Done",
            ChecklistTabKind.Done,
            int.MaxValue);

        RefreshVisibleTabs();
        SelectedTab = VisibleTabs.FirstOrDefault();
    }

    private void AddSystemTab(
        ChecklistTabState? state,
        string id,
        string name,
        ChecklistTabKind kind,
        int order)
    {
        Tabs.Add(new ChecklistTab(
            id,
            name,
            kind,
            state?.IsVisible ?? true,
            order));
    }

    public void SelectTab(ChecklistTab? tab)
    {
        if (tab is null || !tab.IsVisible || !Tabs.Contains(tab)) return;
        SelectedTab = tab;
    }

    public ChecklistTab? FindTab(string id) =>
        Tabs.FirstOrDefault(tab => string.Equals(tab.Id, id, StringComparison.Ordinal));

    public bool SetDefaultTabVisibility(string id, bool isVisible)
    {
        var tab = FindTab(id);
        if (tab is null || !tab.IsSystem || tab.IsVisible == isVisible)
            return tab is not null;

        tab.IsVisible = isVisible;
        if (!TrySaveTabs())
        {
            tab.IsVisible = !isVisible;
            return false;
        }

        RefreshVisibleTabs();
        return true;
    }

    public bool TryCreateTab(string name, out ChecklistTab? createdTab)
    {
        createdTab = null;
        var normalizedName = NormalizeTabName(name);
        if (!IsTabNameAvailable(normalizedName, null))
            return false;

        var tab = new ChecklistTab(
            Guid.NewGuid().ToString("N"),
            normalizedName,
            ChecklistTabKind.Custom,
            isVisible: true,
            _nextCustomTabOrder++);
        Tabs.Add(tab);
        if (!TrySaveTabs())
        {
            Tabs.Remove(tab);
            _nextCustomTabOrder--;
            return false;
        }

        RefreshVisibleTabs();
        SelectTab(tab);
        createdTab = tab;
        return true;
    }

    public bool TryRenameTab(ChecklistTab tab, string name)
    {
        if (!tab.CanDelete || !Tabs.Contains(tab)) return false;
        var normalizedName = NormalizeTabName(name);
        if (!IsTabNameAvailable(normalizedName, tab)) return false;

        var previousName = tab.Name;
        tab.Name = normalizedName;
        if (TrySaveTabs()) return true;

        tab.Name = previousName;
        return false;
    }

    public bool TryDeleteTab(ChecklistTab tab)
    {
        if (!tab.CanDelete || !Tabs.Contains(tab)) return false;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (!TrySaveTabs())
        {
            Tabs.Insert(index, tab);
            return false;
        }

        RunBulkUpdate(() =>
        {
            foreach (var item in Items.Where(item => item.TabId == tab.Id))
                item.TabId = null;
        });
        RefreshVisibleTabs();
        return true;
    }

    public void MoveItemToTab(ChecklistItem item, string? tabId)
    {
        if (!Items.Contains(item)) return;
        var validTabId = tabId is not null &&
                         CustomTabs.Any(tab => tab.Id == tabId)
            ? tabId
            : null;
        item.TabId = validTabId;
    }

    public bool IsTabNameAvailable(string name, ChecklistTab? existingTab)
    {
        var normalizedName = NormalizeTabName(name);
        return !string.IsNullOrWhiteSpace(normalizedName) &&
               !Tabs.Any(tab =>
                   !ReferenceEquals(tab, existingTab) &&
                   string.Equals(tab.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTabName(string? name)
    {
        var normalized = name?.Trim() ?? "";
        return normalized.Length <= 30 ? normalized : normalized[..30].TrimEnd();
    }

    private static string MakeUniqueTabName(string name, ISet<string> usedNames)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Tab" : name;
        var nameToTry = baseName;
        var suffix = 2;
        while (!usedNames.Add(nameToTry))
        {
            var suffixText = $" ({suffix++})";
            var prefixLength = Math.Max(1, 30 - suffixText.Length);
            nameToTry = $"{baseName[..Math.Min(baseName.Length, prefixLength)].TrimEnd()}{suffixText}";
        }
        return nameToTry;
    }

    private void RefreshVisibleTabs()
    {
        var previousSelection = SelectedTab;
        VisibleTabs.Clear();
        foreach (var tab in Tabs.Where(tab => tab.IsVisible).OrderBy(tab => tab.Order))
            VisibleTabs.Add(tab);

        if (previousSelection is not null && VisibleTabs.Contains(previousSelection))
        {
            SelectedTab = previousSelection;
        }
        else
        {
            SelectedTab = VisibleTabs.FirstOrDefault();
        }

        OnPropertyChanged(nameof(CanAddItem));
    }

    // --- Collection / item change handlers ---

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

    // --- Filtering / sorting ---

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

            // In-place sync to minimise ListBox re-renders
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

    // --- CRUD ---

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

    // --- Bulk operations ---

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

    // --- Reorder ---

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

    // --- Persistence ---

    public bool TryFlushPendingItems(out string? error)
    {
        return _saveScheduler.TryFlush(out error);
    }

    public void Dispose()
    {
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

    private IReadOnlyList<ChecklistTabState> CreateTabsSnapshot()
    {
        return Tabs.Select(tab => new ChecklistTabState
        {
            Id = tab.Id,
            Name = tab.Name,
            Kind = tab.Kind,
            IsVisible = tab.IsVisible,
            Order = tab.Order
        }).ToList();
    }

    private bool TrySaveTabs()
    {
        try
        {
            var saveResult = _contentService.SaveTabs(CreateTabsSnapshot());
            PersistenceError = saveResult.Warning;
            return true;
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            PersistenceError = $"Checklist tabs could not be saved: {ex.Message}";
            return false;
        }
    }

    private PersistenceSaveResult SaveItemsSnapshot(IReadOnlyList<ChecklistItemState> items)
    {
        try
        {
            var saveResult = _contentService.SaveItems(items);
            PersistenceError = saveResult.Warning;
            return PersistenceSaveResult.Succeeded();
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            PersistenceError = $"Checklist content could not be saved: {ex.Message}";
            return PersistenceSaveResult.Failed(PersistenceError);
        }
    }

    private void SetLoadIssues(IReadOnlyList<ChecklistContentIssue> issues)
    {
        PersistenceError = issues.Count == 0
            ? null
            : string.Join(
                " ",
                issues.Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"));
    }

    private static bool IsExpectedPersistenceException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
