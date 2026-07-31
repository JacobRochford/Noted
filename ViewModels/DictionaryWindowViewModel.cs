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
    private bool _ghostModeEnabled;

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

    public DictionaryWindowViewModel(
        IAppSettingsService settingsService,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled)
    {
        _settingsService = settingsService;
        _uiThreadInvoke = uiThreadInvoke;
        Items = new ObservableCollection<DictionaryItem>();
        _ghostModeEnabled = ghostModeEnabled;

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

    public DictionaryItem AddItem()
    {
        var newItem = new DictionaryItem { Word = "", Description = "" };
        _uiThreadInvoke(() =>
        {
            Items.Add(newItem);
        });
        return newItem;
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
        var itemsData = Items.Select(i => new DictionaryItemState { Word = i.Word, Description = i.Description }).ToList();
        _settingsService.SaveDictionaryItems(itemsData);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
