using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows.Data;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public sealed class DictionaryWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan s_saveQuietPeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan s_saveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly IDictionaryContentService _contentService;
    private readonly SaveScheduler<SaveRequest> _saveScheduler;
    private readonly Action<Action> _uiThreadInvoke;
    private IReadOnlyList<DictionaryItemState>? _publishingSavedItems;
    private bool _ghostModeEnabled;
    private string _searchText = "";
    private string? _persistenceError;

    public ObservableCollection<DictionaryItem> Items { get; }
    public ICollectionView FilteredItems { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? "";
            FilteredItems.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    public string EmptyMessage => Items.Count == 0
        ? "No words yet"
        : "No matching words";

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set
        {
            if (_ghostModeEnabled != value)
            {
                _ghostModeEnabled = value;
                OnPropertyChanged();
            }
        }
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

    public DictionaryWindowViewModel(
        IDictionaryContentService contentService,
        Dispatcher dispatcher,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(uiThreadInvoke);
        _contentService = contentService;
        _saveScheduler = new SaveScheduler<SaveRequest>(
            dispatcher,
            s_saveQuietPeriod,
            s_saveMaximumDelay,
            SaveItemsSnapshot);
        _uiThreadInvoke = uiThreadInvoke;
        Items = new ObservableCollection<DictionaryItem>();
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = MatchesSearch;
        _ghostModeEnabled = ghostModeEnabled;

        var loadResult = _contentService.LoadItems();
        RecoveryIssuesFoundThisRun = loadResult.Issues.Count > 0;
        SetLoadIssues(loadResult.Issues);
        foreach (var item in loadResult.Items)
        {
            Items.Add(new DictionaryItem
            {
                Word = item.Word ?? "",
                Definition = item.Description ?? ""
            });
        }

        Items.CollectionChanged += Items_CollectionChanged;

        // Subscribe to existing items
        foreach (var item in Items)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DictionaryItem.Word) or nameof(DictionaryItem.Definition))) return;
        if (!string.IsNullOrWhiteSpace(SearchText))
            FilteredItems.Refresh();
        SaveItems();
    }

    public DictionaryItem AddItem(bool beginEdit = false, bool autoSave = false)
    {
        SearchText = "";
        var newItem = new DictionaryItem { Word = "", Definition = "", IsNew = beginEdit && !autoSave };
        if (beginEdit) newItem.BeginEdit(autoSave);
        _uiThreadInvoke(() =>
        {
            Items.Add(newItem);
        });
        return newItem;
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DictionaryItem item in e.NewItems)
                item.PropertyChanged += Item_PropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (DictionaryItem item in e.OldItems)
                item.PropertyChanged -= Item_PropertyChanged;
        }

        SaveItems();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool MatchesSearch(object item)
    {
        if (item is not DictionaryItem dictionaryItem)
            return false;

        var searchText = SearchText.Trim();
        return searchText.Length == 0 ||
               dictionaryItem.Word.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               dictionaryItem.Definition.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    public void RemoveItem(DictionaryItem item)
    {
        _uiThreadInvoke(() =>
        {
            Items.Remove(item);
        });
    }

    public bool TryFlushPendingItems(out string? error)
    {
        return _saveScheduler.TryFlush(out error);
    }

    public bool TrySaveEntry(DictionaryItem item, out string? error)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            throw new ArgumentException("The entry does not belong to this dictionary.", nameof(item));

        _saveScheduler.Schedule(new SaveRequest(item, new DictionaryItemState
        {
            Word = item.DraftWord,
            Description = item.DraftDefinition
        }));
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
        // publishing a verified save does not need another identical write.
        if (_publishingSavedItems is not null &&
            _publishingSavedItems.SequenceEqual(CreateItemsSnapshot(new SaveRequest())))
            return;

        _saveScheduler.Schedule(new SaveRequest());
    }

    private List<DictionaryItemState> CreateItemsSnapshot(SaveRequest request) => Items
        .Where(item => ReferenceEquals(item, request.EditedItem) ||
            (!item.IsNew && (!item.IsAutoSaving ||
                !string.IsNullOrWhiteSpace(item.Word) || !string.IsNullOrWhiteSpace(item.Definition))))
        .Select(item => ReferenceEquals(item, request.EditedItem)
            ? request.Edit!
            : new DictionaryItemState
            {
                Word = item.Word,
                Description = item.Definition
            })
        .ToList();

    private PersistenceSaveResult SaveItemsSnapshot(SaveRequest request)
    {
        // build authorized values at execution time so reentrant changes cannot leave a stale retry.
        var items = CreateItemsSnapshot(request);
        DictionaryContentSaveResult saveResult;
        try
        {
            saveResult = _contentService.SaveItems(items);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            // withdraw the explicit candidate before an error notification can trigger another flush.
            if (request.EditedItem is not null)
                _saveScheduler.Schedule(new SaveRequest());
            ExceptionDiagnostics.Record(ex);
            var error = ex is FileVerificationException
                ? "Dictionary content was written but could not be verified. Disk contents are uncertain. " +
                  "Cancel only discards the draft; it does not undo the disk write. " +
                  "Resolve the file error and restart Noted before retrying."
                : $"Dictionary content could not be saved. {ex.Message}";
            PersistenceError = error;
            return PersistenceSaveResult.Failed(error);
        }

        if (request.EditedItem is not null)
        {
            var previousItems = _publishingSavedItems;
            _publishingSavedItems = items;
            try
            {
                request.EditedItem.SaveEdit(request.Edit!.Word ?? "", request.Edit.Description ?? "");
            }
            finally
            {
                _publishingSavedItems = previousItems;
            }
        }
        PersistenceError = saveResult.Warning;
        return PersistenceSaveResult.Succeeded(saveResult.Warning);
    }

    private sealed record SaveRequest(DictionaryItem? EditedItem = null, DictionaryItemState? Edit = null);

    private void SetLoadIssues(IReadOnlyList<DictionaryContentIssue> issues)
    {
        PersistenceError = issues.Count == 0
            ? null
            : string.Join(
                " ",
                issues.Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"));
    }

    private static bool IsExpectedPersistenceException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);

}
