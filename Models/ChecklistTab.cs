using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Noted.Models;

public enum ChecklistTabKind
{
    All,
    Urgent,
    Done,
    Custom
}

public sealed class ChecklistTab : INotifyPropertyChanged
{
    public const string AllId = "all";
    public const string UrgentId = "urgent";
    public const string DoneId = "done";

    private string _name;
    private bool _isVisible;
    private bool _isSelected;

    public ChecklistTab(string id, string name, ChecklistTabKind kind, bool isVisible, int order)
    {
        Id = id;
        _name = name;
        Kind = kind;
        _isVisible = isVisible;
        Order = order;
    }

    public string Id { get; }
    public ChecklistTabKind Kind { get; }
    public int Order { get; }
    public bool IsSystem => Kind != ChecklistTabKind.Custom;
    public bool CanDelete => Kind == ChecklistTabKind.Custom;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
