using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Noted.Models;

namespace Noted.Services;

public sealed class JotsWorkspace : ObservableObject, IDisposable
{
    private readonly IJotsContentService _contentService;
    private readonly SaveScheduler<IReadOnlyList<JotItemState>> _saveScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly ObservableCollection<JotItem> _items = [];
    private bool _loaded;
    private string? _persistenceError;

    public ObservableCollection<JotItem> Items
    {
        get { EnsureLoaded(); return _items; }
    }

    public string? PersistenceError
    {
        get => _persistenceError;
        private set => SetProperty(ref _persistenceError, value);
    }

    internal bool RecoveryIssuesFoundThisRun { get; private set; }
    internal bool IsLoaded => _loaded;

    public JotsWorkspace(IJotsContentService contentService, Dispatcher dispatcher, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _contentService = contentService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _saveScheduler = new SaveScheduler<IReadOnlyList<JotItemState>>(
            dispatcher, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(2), SaveSnapshot);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        var result = _contentService.LoadItems();
        RecoveryIssuesFoundThisRun = result.Issues.Count > 0;
        PersistenceError = result.Issues.Count == 0 ? null : string.Join(" ",
            result.Issues.Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"));
        foreach (var state in result.Items)
        {
            var created = state.CreatedUtc == default ? _timeProvider.GetUtcNow() : state.CreatedUtc;
            var item = new JotItem
            {
                Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id,
                Title = state.Title ?? "", Details = state.Details ?? "", Category = state.Category ?? "",
                IsPinned = state.IsPinned, IsStarred = state.IsStarred,
                CreatedUtc = created, ModifiedUtc = state.ModifiedUtc == default ? created : state.ModifiedUtc
            };
            _items.Add(item);
            item.PropertyChanged += Item_PropertyChanged;
        }
        _items.CollectionChanged += Items_CollectionChanged;
        _loaded = true;
    }

    internal bool TryCapture(Guid id, string text, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Write something before saving the jot.";
            return false;
        }

        EnsureLoaded();
        // the saved draft ID prevents duplicates if the app crashes before clearing the draft.
        if (_items.Any(item => item.Id == id))
        {
            error = null;
            return true;
        }
        if (!TryFlush(out error))
            return false;

        var now = _timeProvider.GetUtcNow();
        var captured = new JotItem { Id = id, CreatedUtc = now, ModifiedUtc = now, IsNew = true };
        captured.BeginEdit();
        captured.DraftDetails = text;
        captured.SaveEdit(now);
        var snapshot = _items.Select(ToState).Append(ToState(captured)).ToList();
        var result = SaveSnapshot(snapshot);
        if (!result.Success)
        {
            error = result.Error;
            return false;
        }

        _items.Add(captured);
        _saveScheduler.CancelPending();
        error = null;
        return true;
    }

    public bool TryFlush(out string? error) => _saveScheduler.TryFlush(out error);

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (JotItem item in e.NewItems) item.PropertyChanged += Item_PropertyChanged;
        if (e.OldItems is not null)
            foreach (JotItem item in e.OldItems) item.PropertyChanged -= Item_PropertyChanged;
        ScheduleSave();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JotItem.Title) or nameof(JotItem.Details) or nameof(JotItem.Category) or
            nameof(JotItem.IsPinned) or nameof(JotItem.IsStarred) or nameof(JotItem.ModifiedUtc))
            ScheduleSave();
    }

    private void ScheduleSave() => _saveScheduler.Schedule(_items.Select(ToState).ToList());

    private static JotItemState ToState(JotItem item) => new()
    {
        Id = item.Id, Title = item.Title, Details = item.Details, Category = item.Category,
        IsPinned = item.IsPinned, IsStarred = item.IsStarred,
        CreatedUtc = item.CreatedUtc, ModifiedUtc = item.ModifiedUtc
    };

    private PersistenceSaveResult SaveSnapshot(IReadOnlyList<JotItemState> snapshot)
    {
        try
        {
            var result = _contentService.SaveItems(snapshot);
            PersistenceError = result.Warning;
            return PersistenceSaveResult.Succeeded(result.Warning);
        }
        catch (Exception ex) when (FileSystemErrors.IsExpected(ex))
        {
            ExceptionDiagnostics.Record(ex);
            PersistenceError = "Jots content could not be saved. Your text has been kept; try again.";
            return PersistenceSaveResult.Failed(PersistenceError);
        }
    }

    public void Dispose()
    {
        _items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in _items) item.PropertyChanged -= Item_PropertyChanged;
        _saveScheduler.Dispose();
    }
}
