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

public sealed class DictionaryWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan SaveQuietPeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SaveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly IDictionaryContentService _contentService;
    private readonly SaveScheduler<IReadOnlyList<DictionaryItemState>> _saveScheduler;
    private readonly Action<Action> _uiThreadInvoke;
    private bool _ghostModeEnabled;
    private string? _persistenceError;

    public ObservableCollection<DictionaryItem> Items { get; }

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
        _saveScheduler = new SaveScheduler<IReadOnlyList<DictionaryItemState>>(
            dispatcher,
            SaveQuietPeriod,
            SaveMaximumDelay,
            SaveItemsSnapshot);
        _uiThreadInvoke = uiThreadInvoke;
        Items = new ObservableCollection<DictionaryItem>();
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
        SaveItems();
    }

    public DictionaryItem AddItem()
    {
        var newItem = new DictionaryItem { Word = "", Definition = "" };
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

    public void Dispose()
    {
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in Items)
            item.PropertyChanged -= Item_PropertyChanged;
        _saveScheduler.Dispose();
    }

    private void SaveItems()
    {
        _saveScheduler.Schedule(Items
            .Select(item => new DictionaryItemState
            {
                Word = item.Word,
                Description = item.Definition
            })
            .ToList());
    }

    private PersistenceSaveResult SaveItemsSnapshot(IReadOnlyList<DictionaryItemState> items)
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
            PersistenceError = $"Dictionary content could not be saved: {ex.Message}";
            return PersistenceSaveResult.Failed(PersistenceError);
        }
    }

    private void SetLoadIssues(IReadOnlyList<DictionaryContentIssue> issues)
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

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
