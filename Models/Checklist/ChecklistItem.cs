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
    private DateTimeOffset? _reminderAtUtc;
    private DateTimeOffset? _reminderHandledAtUtc;
    private string _notes = "";
    private bool _isExpanded;
    private bool _isEditingTitle;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            if (value) CancelReminder();
            OnPropertyChanged();
        }
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

    public DateTimeOffset? ReminderAtUtc
    {
        get => _reminderAtUtc;
        set
        {
            var reminder = _isChecked ? null : value?.ToUniversalTime();
            if (_reminderAtUtc == reminder) return;
            _reminderAtUtc = reminder;
            ReminderHandledAtUtc = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveReminder));
        }
    }

    public DateTimeOffset? ReminderHandledAtUtc
    {
        get => _reminderHandledAtUtc;
        set
        {
            if (SetProperty(ref _reminderHandledAtUtc,
                _reminderAtUtc.HasValue && !_isChecked ? value?.ToUniversalTime() : null))
                OnPropertyChanged(nameof(HasActiveReminder));
        }
    }

    public bool HasActiveReminder => !_isChecked && _reminderAtUtc.HasValue && _reminderHandledAtUtc is null;

    internal void CancelReminder() => ReminderAtUtc = null;

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

    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        set => SetProperty(ref _isEditingTitle, value);
    }

}
