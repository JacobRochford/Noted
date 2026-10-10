using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public sealed class JotsWindowViewModel : ObservableObject, IDisposable
{
    private enum JotsViewKind
    {
        All,
        Inbox,
        Pinned,
        Category
    }

    private readonly JotsWorkspace _workspace;
    private readonly bool _ownsWorkspace;
    private readonly Action<Action> _uiThreadInvoke;
    private readonly TimeProvider _timeProvider;
    private bool _ghostModeEnabled;
    private string _searchText = "";
    private string? _persistenceError;
    private JotsViewKind _viewKind;
    private string? _selectedCategory;
    private IReadOnlyList<string> _categories = [];
    private JotItem? _selectedItem;

    public ObservableCollection<JotItem> Items { get; }
    public ICollectionView FilteredItems { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var normalized = value ?? "";
            if (_searchText == normalized)
                return;

            _searchText = normalized;
            RefreshFilter();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    public JotItem? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            OnPropertyChanged(nameof(IsDetailOpen));
        }
    }

    public bool IsDetailOpen => SelectedItem is not null;
    public bool IsAllFilter => _viewKind == JotsViewKind.All;
    public bool IsInboxFilter => _viewKind == JotsViewKind.Inbox;
    public bool IsPinnedFilter => _viewKind == JotsViewKind.Pinned;
    public bool IsCategoryFilter => _viewKind == JotsViewKind.Category;
    public IReadOnlyList<string> Categories => _categories;
    public string CategoryButtonText => IsCategoryFilter && !string.IsNullOrWhiteSpace(_selectedCategory)
        ? _selectedCategory!
        : "Categories";

    public string EmptyMessage
    {
        get
        {
            if (Items.Count == 0)
                return "Nothing here yet";
            if (!string.IsNullOrWhiteSpace(SearchText))
                return "No matching jots";

            return _viewKind switch
            {
                JotsViewKind.Inbox => "Inbox is empty",
                JotsViewKind.Pinned => "Nothing pinned yet",
                JotsViewKind.Category when !string.IsNullOrWhiteSpace(_selectedCategory) =>
                    $"No jots in {_selectedCategory}",
                _ => "No jots here"
            };
        }
    }

    public string StatusText
    {
        get
        {
            var shown = FilteredItems.Cast<object>().Count();
            var total = Items.Count;
            if (shown == total && string.IsNullOrWhiteSpace(SearchText) && _viewKind == JotsViewKind.All)
                return $"{total} {(total == 1 ? "jot" : "jots")}";

            return $"{shown} of {total} shown";
        }
    }

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set
        {
            if (_ghostModeEnabled == value)
                return;

            _ghostModeEnabled = value;
            OnPropertyChanged();
        }
    }

    public string? PersistenceError
    {
        get => _persistenceError;
        private set
        {
            if (_persistenceError == value)
                return;

            _persistenceError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPersistenceError));
        }
    }

    public bool HasPersistenceError => !string.IsNullOrWhiteSpace(PersistenceError);
    internal bool RecoveryIssuesFoundThisRun => _workspace.RecoveryIssuesFoundThisRun;

    public JotsWindowViewModel(
        IJotsContentService contentService,
        Dispatcher dispatcher,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled,
        TimeProvider? timeProvider = null)
        : this(new JotsWorkspace(contentService, dispatcher, timeProvider), ghostModeEnabled, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(uiThreadInvoke);
        _uiThreadInvoke = uiThreadInvoke;
        _ownsWorkspace = true;
    }

    public JotsWindowViewModel(JotsWorkspace workspace, bool ghostModeEnabled, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _uiThreadInvoke = action => action();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ghostModeEnabled = ghostModeEnabled;
        Items = workspace.Items;
        // each window needs its own filters over the shared items.
        FilteredItems = new ListCollectionView(Items);
        FilteredItems.Filter = MatchesCurrentView;
        FilteredItems.SortDescriptions.Add(new SortDescription(nameof(JotItem.IsPinned), ListSortDirection.Descending));
        FilteredItems.SortDescriptions.Add(new SortDescription(nameof(JotItem.ModifiedUtc), ListSortDirection.Descending));
        PersistenceError = workspace.PersistenceError;
        workspace.PropertyChanged += Workspace_PropertyChanged;
        Items.CollectionChanged += Items_CollectionChanged;
        foreach (var item in Items)
            item.PropertyChanged += Item_PropertyChanged;
        RebuildCategories();
    }

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JotsWorkspace.PersistenceError))
            PersistenceError = _workspace.PersistenceError;
    }

    public void SelectAll() => SetView(JotsViewKind.All, null);
    public void SelectInbox() => SetView(JotsViewKind.Inbox, null);
    public void SelectPinned() => SetView(JotsViewKind.Pinned, null);

    public void SelectCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            SelectInbox();
            return;
        }

        SetView(JotsViewKind.Category, category.Trim());
    }

    public void OpenItem(JotItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            return;

        SelectedItem = item;
    }

    public JotItem BeginNewItem()
    {
        var now = GetUtcNow();
        var item = new JotItem
        {
            Id = Guid.NewGuid(),
            CreatedUtc = now,
            ModifiedUtc = now,
            IsNew = true
        };
        item.BeginEdit();
        SelectedItem = item;
        return item;
    }

    public void BeginEditSelected()
    {
        SelectedItem?.BeginEdit();
    }

    public bool TrySaveSelected(out string? error)
    {
        var item = SelectedItem;
        if (item is null || !item.IsEditing)
        {
            error = null;
            return true;
        }

        if (!item.CanSaveDraft)
        {
            error = "Add a title or some details before saving the jot.";
            return false;
        }

        if (!item.IsNew && !Items.Contains(item))
        {
            error = "This jot has been deleted.";
            return false;
        }

        var wasNew = item.IsNew;
        item.SaveEdit(GetUtcNow());
        if (wasNew)
            _uiThreadInvoke(() => Items.Add(item));

        RefreshFilter();
        error = null;
        return true;
    }

    public void CancelSelectedEdit()
    {
        var item = SelectedItem;
        if (item is null || !item.IsEditing)
            return;

        var wasNew = item.IsNew;
        item.CancelEdit();
        if (wasNew)
            SelectedItem = null;
    }

    public void CloseDetail()
    {
        SelectedItem = null;
    }

    public void RemoveItem(JotItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _uiThreadInvoke(() => Items.Remove(item));
        if (ReferenceEquals(SelectedItem, item))
            SelectedItem = null;
    }

    public void TogglePinned(JotItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            return;

        item.TogglePinned(GetUtcNow());
    }

    public void ToggleStarred(JotItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            return;

        item.ToggleStarred(GetUtcNow());
    }

    public bool TryFlushPendingItems(out string? error) => _workspace.TryFlush(out error);

    public void Dispose()
    {
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in Items)
            item.PropertyChanged -= Item_PropertyChanged;
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
        if (_ownsWorkspace)
            _workspace.Dispose();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (JotItem item in e.NewItems)
                item.PropertyChanged += Item_PropertyChanged;
        }

        if (e.OldItems is not null)
        {
            foreach (JotItem item in e.OldItems)
                item.PropertyChanged -= Item_PropertyChanged;
        }

        if (SelectedItem is { IsNew: false } selected && !Items.Contains(selected))
        {
            selected.CancelEdit();
            SelectedItem = null;
        }
        RebuildCategories();
        RefreshFilter();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(JotItem.Title) or
            nameof(JotItem.Details) or
            nameof(JotItem.Category) or
            nameof(JotItem.IsPinned) or
            nameof(JotItem.IsStarred) or
            nameof(JotItem.ModifiedUtc)))
        {
            return;
        }

        if (e.PropertyName == nameof(JotItem.Category))
            RebuildCategories();

        RefreshFilter();
    }

    private void SetView(JotsViewKind viewKind, string? category)
    {
        if (_viewKind == viewKind && string.Equals(_selectedCategory, category, StringComparison.OrdinalIgnoreCase))
            return;

        _viewKind = viewKind;
        _selectedCategory = category;
        RefreshFilter();
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsInboxFilter));
        OnPropertyChanged(nameof(IsPinnedFilter));
        OnPropertyChanged(nameof(IsCategoryFilter));
        OnPropertyChanged(nameof(CategoryButtonText));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool MatchesCurrentView(object candidate)
    {
        if (candidate is not JotItem item)
            return false;

        var matchesView = _viewKind switch
        {
            JotsViewKind.All => true,
            JotsViewKind.Inbox => string.IsNullOrWhiteSpace(item.Category),
            JotsViewKind.Pinned => item.IsPinned,
            JotsViewKind.Category => string.Equals(
                item.Category,
                _selectedCategory,
                StringComparison.OrdinalIgnoreCase),
            _ => true
        };
        if (!matchesView)
            return false;

        var search = SearchText.Trim();
        return search.Length == 0 ||
               item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.Details.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.Category.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilter()
    {
        FilteredItems.Refresh();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void RebuildCategories()
    {
        _categories = Items
            .Select(item => item.Category.Trim())
            .Where(category => category.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        OnPropertyChanged(nameof(Categories));
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
}
