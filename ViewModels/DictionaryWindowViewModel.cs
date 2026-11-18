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
    private readonly DictionaryDraftStore? _draftStore;
    private readonly SaveScheduler<object>? _draftScheduler;
    private readonly Dictionary<Guid, DictionaryDraftState> _draftRecords = [];
    private readonly Dictionary<DictionaryItem, Guid> _draftIds = [];
    private IReadOnlyDictionary<Guid, DictionaryItemState> _verifiedEntries =
        new Dictionary<Guid, DictionaryItemState>();
    private bool _publishingDraft;
    private bool _draftLoadFailed;
    private string? _draftError;
    private static readonly object s_draftSaveRequest = new();
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
        get => _draftError ?? _persistenceError;
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
        bool ghostModeEnabled) : this(contentService, dispatcher, uiThreadInvoke, ghostModeEnabled, null)
    {
    }

    internal DictionaryWindowViewModel(
        IDictionaryContentService contentService,
        Dispatcher dispatcher,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled,
        DictionaryDraftStore? draftStore)
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
        _draftStore = draftStore;
        if (draftStore is not null)
            _draftScheduler = new(dispatcher, s_saveQuietPeriod, s_saveMaximumDelay, _ => SaveDrafts());
        Items = new ObservableCollection<DictionaryItem>();
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = MatchesSearch;
        _ghostModeEnabled = ghostModeEnabled;

        var loadResult = _contentService.LoadItems();
        SetLoadIssues(loadResult.Issues);
        // ambiguous IDs from the input cannot identify a draft's original item.
        _verifiedEntries = loadResult.Items.Where(item => item.Id != Guid.Empty)
            .GroupBy(item => item.Id).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var reservedIds = loadResult.Items.Select(item => item.Id).Where(id => id != Guid.Empty).ToHashSet();
        var seenIds = new HashSet<Guid>();
        var repairedIds = false;
        foreach (var item in loadResult.Items)
        {
            var id = item.Id;
            if (id == Guid.Empty || !seenIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid();
                }
                while (!reservedIds.Add(id));
                repairedIds = true;
            }

            Items.Add(new DictionaryItem
            {
                Id = id,
                Word = item.Word ?? "",
                Definition = item.Description ?? ""
            });
        }

        var draftRecoveryIssue = RestoreDrafts();
        RecoveryIssuesFoundThisRun = loadResult.Issues.Count > 0 || draftRecoveryIssue;
        Items.CollectionChanged += Items_CollectionChanged;

        // Subscribe to existing items
        foreach (var item in Items)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }

        if (repairedIds) SaveItems();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DictionaryItem.HasPendingEdit) && !_publishingDraft &&
            sender is DictionaryItem draft && (draft.HasPendingEdit || _draftIds.ContainsKey(draft)))
            ScheduleDrafts();
        if (e.PropertyName is not (nameof(DictionaryItem.Word) or nameof(DictionaryItem.Definition))) return;
        if (!string.IsNullOrWhiteSpace(SearchText) && sender is not DictionaryItem { IsAutoSaving: true })
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

        if (e.Action == NotifyCollectionChangedAction.Reset ||
            e.NewItems?.OfType<DictionaryItem>().Any(item => !item.IsNew) == true ||
            e.OldItems?.OfType<DictionaryItem>().Any(item => !item.IsNew) == true)
            SaveItems();
        if (e.NewItems?.OfType<DictionaryItem>().Any(item => item.HasPendingEdit) == true)
            ScheduleDrafts();
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool MatchesSearch(object item)
    {
        if (item is not DictionaryItem dictionaryItem)
            return false;

        var searchText = SearchText.Trim();
        return dictionaryItem.IsNew || dictionaryItem.IsAutoSaving || searchText.Length == 0 ||
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
        var contentSaved = _saveScheduler.TryFlush(out var contentError);
        var draftsSaved = TryFlushDrafts(out var draftError);
        error = contentError ?? draftError;
        return contentSaved && draftsSaved;
    }

    internal bool TryFlushDrafts(out string? error)
    {
        if (_draftScheduler is not null && !_draftScheduler.TryFlush(out error)) return false;
        error = _draftError;
        return error is null;
    }

    internal bool TryCancelEntry(DictionaryItem item, out string? error)
    {
        if (_draftStore is not null)
        {
            TrackDraft(item);
            if (_draftIds.TryGetValue(item, out var id))
            {
                var result = SaveDrafts(retire: id);
                error = result.Error;
                if (!result.Success) return false;
                _draftIds.Remove(item);
            }
        }

        _publishingDraft = true;
        try
        {
            item.CancelEdit();
            if (item.IsNew) RemoveItem(item);
        }
        finally { _publishingDraft = false; }
        error = null;
        return true;
    }

    public bool TrySaveEntry(DictionaryItem item, out string? error)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            throw new ArgumentException("The entry does not belong to this dictionary.", nameof(item));

        var word = item.DraftWord;
        var definition = item.DraftDefinition;
        if (!TryFlushDrafts(out error)) return false;
        _draftIds.TryGetValue(item, out var draftId);
        var draft = _draftRecords.GetValueOrDefault(draftId);
        var savedRevision = draft is not null && draft.Word == word && draft.Definition == definition
            ? draft.RevisionId
            : _verifiedEntries.GetValueOrDefault(item.Id)?.SavedDraftRevisionId;
        _saveScheduler.Schedule(new SaveRequest(item, new DictionaryItemState
        {
            Id = item.Id,
            SavedDraftRevisionId = savedRevision,
            Word = word,
            Description = definition
        }));
        if (!_saveScheduler.TryFlush(out error)) return false;
        // the accepted entry carries the receipt even if this cleanup has to be retried.
        TryFlushDrafts(out _);
        return true;
    }

    public void Dispose()
    {
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in Items)
            item.PropertyChanged -= Item_PropertyChanged;
        _saveScheduler.Dispose();
        _draftScheduler?.Dispose();
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
            (!item.IsNew && (!item.IsAutoSaving || !item.IsEmpty)))
        .Select(item => ReferenceEquals(item, request.EditedItem)
            ? request.Edit!
            : new DictionaryItemState
            {
                Id = item.Id,
                SavedDraftRevisionId = _verifiedEntries.GetValueOrDefault(item.Id)?.SavedDraftRevisionId,
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
            _verifiedEntries = items.ToDictionary(item => item.Id);
            var draftId = _draftIds.GetValueOrDefault(request.EditedItem);
            var retiredDraft = _draftRecords.TryGetValue(draftId, out var draft) &&
                draft.RevisionId == request.Edit!.SavedDraftRevisionId;
            if (retiredDraft)
            {
                _draftIds.Remove(request.EditedItem);
                _draftRecords[draftId] = DictionaryDraftState.Retire(draftId);
            }
            _publishingDraft = true;
            try
            {
                request.EditedItem.SaveEdit(request.Edit!.Word ?? "", request.Edit.Description ?? "");
            }
            finally
            {
                _publishingDraft = false;
                _publishingSavedItems = previousItems;
            }
            if (retiredDraft || request.EditedItem.HasPendingEdit) ScheduleDrafts();
        }
        else _verifiedEntries = items.ToDictionary(item => item.Id);
        PersistenceError = saveResult.Warning;
        return PersistenceSaveResult.Succeeded(saveResult.Warning);
    }

    private sealed record SaveRequest(DictionaryItem? EditedItem = null, DictionaryItemState? Edit = null);

    private void ScheduleDrafts() => _draftScheduler?.Schedule(s_draftSaveRequest);

    private void TrackDraft(DictionaryItem item)
    {
        if (item.HasPendingEdit && !_draftIds.ContainsKey(item))
            _draftIds.Add(item, Guid.NewGuid());
    }

    private PersistenceSaveResult SaveDrafts(Guid? retire = null)
    {
        if (_draftLoadFailed)
            return PersistenceSaveResult.Failed(_draftError ?? "The Dictionary draft file could not be loaded.");
        try
        {
            foreach (var item in Items) TrackDraft(item);
            if (_draftIds.Any(pair => pair.Value != retire && !pair.Key.IsNew &&
                !_verifiedEntries.ContainsKey(pair.Key.Id)) && !_saveScheduler.TryFlush(out var error))
                return FailDraftSave(error ?? "Dictionary entry IDs could not be saved.");

            var records = new Dictionary<Guid, DictionaryDraftState>(_draftRecords);
            foreach (var (item, id) in _draftIds)
            {
                if (id == retire) continue;
                var baseline = item.IsNew ? null : _verifiedEntries.GetValueOrDefault(item.Id) ??
                    throw new IOException("The Dictionary entry ID has not been verified.");
                if (records.TryGetValue(id, out var previous) && previous.EntryId == item.Id &&
                    previous.Word == item.DraftWord && previous.Definition == item.DraftDefinition &&
                    previous.Baseline == baseline) continue;
                records[id] = new()
                {
                    DraftId = id, RevisionId = Guid.NewGuid(), EntryId = item.Id,
                    Baseline = baseline, Word = item.DraftWord, Definition = item.DraftDefinition
                };
            }
            if (retire is { } retiredId) records[retiredId] = DictionaryDraftState.Retire(retiredId);
            var warning = _draftStore!.Save(records.Values.ToArray());
            foreach (var (id, record) in records) _draftRecords[id] = record;
            SetDraftError(null);
            return PersistenceSaveResult.Succeeded(warning);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return FailDraftSave($"Dictionary drafts could not be saved. {ex.Message}");
        }
    }

    private PersistenceSaveResult FailDraftSave(string error)
    {
        SetDraftError(error);
        return PersistenceSaveResult.Failed(error);
    }

    private void SetDraftError(string? error)
    {
        if (_draftError == error) return;
        _draftError = error;
        OnPropertyChanged(nameof(PersistenceError));
        OnPropertyChanged(nameof(HasPersistenceError));
    }

    private bool RestoreDrafts()
    {
        if (_draftStore is null) return false;
        var result = _draftStore.Load();
        if (result.Issues.Count > 0)
        {
            _draftLoadFailed = true;
            SetDraftError(string.Join(" ", result.Issues.Select(issue => issue.Message)));
        }
        var committedDrafts = _verifiedEntries.Values.Select(item => item.SavedDraftRevisionId).ToHashSet();
        var conflicts = false;
        foreach (var draft in result.Items ?? [])
        {
            if (draft.IsRetired || committedDrafts.Contains(draft.RevisionId))
            {
                _draftRecords.Add(draft.DraftId, DictionaryDraftState.Retire(draft.DraftId));
                if (!draft.IsRetired) ScheduleDrafts();
                continue;
            }
            var item = Items.FirstOrDefault(item => item.Id == draft.EntryId);
            if (item is null || item.IsEditing || draft.Baseline is null ||
                _verifiedEntries.GetValueOrDefault(draft.EntryId) != draft.Baseline)
            {
                var detached = draft.Baseline is not null || item is not null;
                item = detached ? new DictionaryItem { IsNew = true } :
                    new DictionaryItem { Id = draft.EntryId, IsNew = true };
                Items.Add(item);
                if (detached)
                {
                    conflicts = true;
                    ScheduleDrafts();
                }
            }
            item.BeginEdit();
            item.DraftWord = draft.Word!;
            item.DraftDefinition = draft.Definition!;
            _draftRecords.Add(draft.DraftId, draft);
            _draftIds.Add(item, draft.DraftId);
        }
        if (conflicts)
            PersistenceError = "A recovered Dictionary draft could not be matched to its saved entry and was opened as a separate draft.";
        return result.Issues.Count > 0;
    }

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
