using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Noted.Models;

public enum ChecklistPriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public sealed class ChecklistItem : INotifyPropertyChanged
{
    private string _text = "";
    private bool _isChecked;
    private ChecklistPriority _priority = ChecklistPriority.None;
    private string? _tabId;
    private DateTime? _dueDate;
    private string _notes = "";
    private bool _isExpanded;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
    }

    public ChecklistPriority Priority
    {
        get => _priority;
        set { if (_priority != value) { _priority = value; OnPropertyChanged(); } }
    }

    public string? TabId
    {
        get => _tabId;
        set { if (_tabId != value) { _tabId = value; OnPropertyChanged(); } }
    }

    public DateTime? DueDate
    {
        get => _dueDate;
        set { if (_dueDate != value) { _dueDate = value; OnPropertyChanged(); } }
    }

    public string Notes
    {
        get => _notes;
        set { if (_notes != value) { _notes = value; OnPropertyChanged(); } }
    }

    // UI-only state — not persisted
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
