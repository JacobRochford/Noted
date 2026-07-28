using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public enum ChecklistFilterMode { All, Active, Completed }
public enum ChecklistSortMode  { Manual, Priority, DueDate, Alphabetical }

public sealed class ChecklistWindowViewModel : INotifyPropertyChanged
{
    private readonly IAppSettingsService _settingsService;
    private readonly Action<Action> _uiThreadInvoke;
    private bool _ghostModeEnabled;
    private string _searchQuery = "";
    private ChecklistFilterMode _filterMode = ChecklistFilterMode.All;
    private ChecklistSortMode _sortMode = ChecklistSortMode.Manual;
    private bool _isSearchVisible;
    private bool _isRefreshing;
    private bool _isBulkUpdating;

    // Source collection — canonical order
    public ObservableCollection<ChecklistItem> Items { get; } = new();

    // Filtered + sorted view shown to the ListBox
    public ObservableCollection<ChecklistItem> FilteredItems { get; } = new();

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

    public ChecklistFilterMode FilterMode
    {
        get => _filterMode;
        set { if (_filterMode != value) { _filterMode = value; OnPropertyChanged(); RefreshFilteredItems(); } }
    }

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
        IAppSettingsService settingsService,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled)
    {
        _settingsService = settingsService;
        _uiThreadInvoke = uiThreadInvoke;
        _ghostModeEnabled = ghostModeEnabled;

        foreach (var d in _settingsService.LoadChecklistItems())
        {
            var item = new ChecklistItem
            {
                Text      = d.Text ?? "",
                IsChecked = d.IsChecked,
                Priority  = d.Priority,
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

            source = FilterMode switch
            {
                ChecklistFilterMode.Active    => source.Where(i => !i.IsChecked),
                ChecklistFilterMode.Completed => source.Where(i =>  i.IsChecked),
                _                             => source
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

    public ChecklistItem AddItem(ChecklistItem? anchor = null)
    {
        var item = new ChecklistItem { CreatedAt = DateTime.Now };
        _uiThreadInvoke(() =>
        {
            PrepareViewForNewItem();

            var anchorIndex = SortMode == ChecklistSortMode.Manual && anchor is not null
                ? Items.IndexOf(anchor)
                : -1;
            if (anchorIndex >= 0)
                Items.Insert(anchorIndex + 1, item);
            else
                Items.Add(item);
        });
        return item;
    }

    public ChecklistItem InsertItemAfter(ChecklistItem anchor)
    {
        return AddItem(anchor);
    }

    private void PrepareViewForNewItem()
    {
        var refreshNeeded = false;
        if (_filterMode == ChecklistFilterMode.Completed)
        {
            _filterMode = ChecklistFilterMode.Active;
            OnPropertyChanged(nameof(FilterMode));
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
            var changedItems = Items.Where(item => item.IsChecked != isChecked).ToList();
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

    // --- Persistence ---

    public void SaveItems()
    {
        var data = Items.Select(i => new ChecklistItemData
        {
            Text      = i.Text,
            IsChecked = i.IsChecked,
            Priority  = i.Priority,
            DueDate   = i.DueDate,
            Notes     = i.Notes,
            CreatedAt = i.CreatedAt
        }).ToList();
        _settingsService.SaveChecklistItems(data);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
