namespace Noted.Models;

public enum ChecklistPriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public sealed class ChecklistItem : ObservableObject
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
        set => SetProperty(ref _text, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public ChecklistPriority Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public string? TabId
    {
        get => _tabId;
        set => SetProperty(ref _tabId, value);
    }

    public DateTime? DueDate
    {
        get => _dueDate;
        set => SetProperty(ref _dueDate, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    // UI-only state — not persisted
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

}
