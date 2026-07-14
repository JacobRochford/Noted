using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public sealed class DictionaryWindowViewModel : INotifyPropertyChanged
{
    private readonly IAppSettingsService _settingsService;
    private readonly Action<Action> _uiThreadInvoke;
    private DictionaryWindowState _windowState;
    private bool _ghostModeEnabled;

    public ObservableCollection<DictionaryItem> Items { get; }

    public DictionaryWindowState WindowState
    {
        get => _windowState;
        set
        {
            if (_windowState != value)
            {
                _windowState = value;
                OnPropertyChanged();
            }
        }
    }

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set
        {
            if (_ghostModeEnabled != value)
            {
                _ghostModeEnabled = value;
                OnPropertyChanged();
                SaveWindowState();
            }
        }
    }

    public DictionaryWindowViewModel(IAppSettingsService settingsService, Action<Action> uiThreadInvoke)
    {
        _settingsService = settingsService;
        _uiThreadInvoke = uiThreadInvoke;
        Items = new ObservableCollection<DictionaryItem>();

        // Load window state
        _windowState = _settingsService.LoadDictionaryWindowState();
        _ghostModeEnabled = _windowState.GhostModeEnabled;

        // Load existing items
        var savedItems = _settingsService.LoadDictionaryItems();
        foreach (var item in savedItems)
        {
            Items.Add(new DictionaryItem
            {
                Word = item.Word ?? "",
                Description = item.Description ?? ""
            });
        }

        // Subscribe to item changes
        Items.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (DictionaryItem item in e.NewItems)
                {
                    item.PropertyChanged += Item_PropertyChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (DictionaryItem item in e.OldItems)
                {
                    item.PropertyChanged -= Item_PropertyChanged;
                }
            }
            SaveItems();
        };

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

    public void AddItem()
    {
        _uiThreadInvoke(() =>
        {
            var newItem = new DictionaryItem { Word = "", Description = "" };
            Items.Add(newItem);
        });
    }

    public void RemoveItem(DictionaryItem item)
    {
        _uiThreadInvoke(() =>
        {
            Items.Remove(item);
        });
    }

    public void SaveItems()
    {
        var itemsData = Items.Select(i => new DictionaryItemData { Word = i.Word, Description = i.Description }).ToList();
        _settingsService.SaveDictionaryItems(itemsData);
    }

    public void SaveWindowState()
    {
        _windowState = _windowState with { GhostModeEnabled = _ghostModeEnabled };
        _settingsService.SaveDictionaryWindowState(_windowState);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
