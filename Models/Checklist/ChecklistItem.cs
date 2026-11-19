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
    private TimeSpan? _dueTime;
    private DateTimeOffset? _reminderAtUtc;
    private bool _reminderAtDueTime;
    private DateTimeOffset? _reminderHandledAtUtc;
    private ChecklistNotificationStyle _notificationStyle;
    private ChecklistNotificationOptions? _notificationOptions;
    private ChecklistReminderOccurrence? _reminderOccurrence;
    private ChecklistReminderRepeat? _reminderRepeat;
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
        set
        {
            if (!SetProperty(ref _dueDate, value)) return;
            if (value is null) DueTime = null;
            OnPropertyChanged(nameof(DueAtLocal));
        }
    }

    public TimeSpan? DueTime
    {
        get => _dueTime;
        set
        {
            var time = _dueDate.HasValue && value >= TimeSpan.Zero && value < TimeSpan.FromDays(1) ? value : null;
            if (SetProperty(ref _dueTime, time)) OnPropertyChanged(nameof(DueAtLocal));
        }
    }

    public DateTime? DueAtLocal => DueDate?.Date.Add(DueTime ?? TimeSpan.Zero);

    internal void RefreshDueStatus() => OnPropertyChanged(nameof(DueAtLocal));

    public DateTimeOffset? ReminderAtUtc
    {
        get => _reminderAtUtc;
        set
        {
            var reminder = _isChecked ? null : value?.ToUniversalTime();
            if (_reminderAtUtc == reminder) return;
            _reminderAtUtc = reminder;
            ReminderRepeat = null;
            ReminderAtDueTime = false;
            ReminderHandledAtUtc = null;
            ReminderOccurrence = reminder.HasValue && NotificationStyle == ChecklistNotificationStyle.Urgent
                ? ChecklistReminderOccurrence.Create(reminder.Value, _reminderOccurrence?.ConfirmationCode) : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveReminder));
        }
    }

    public bool ReminderAtDueTime
    {
        get => _reminderAtDueTime;
        set => SetProperty(ref _reminderAtDueTime, !_isChecked && _reminderAtUtc.HasValue && value);
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

    public ChecklistNotificationOptions? NotificationOptions
    {
        get => _notificationOptions;
        set => SetProperty(ref _notificationOptions, value?.Normalize());
    }

    public ChecklistNotificationStyle NotificationStyle
    {
        get => _notificationStyle;
        set
        {
            if (!Enum.IsDefined(value)) value = ChecklistNotificationStyle.Standard;
            if (!SetProperty(ref _notificationStyle, value)) return;
            if (_reminderAtUtc.HasValue && value == ChecklistNotificationStyle.Urgent && _reminderOccurrence is null)
                ReminderOccurrence = ChecklistReminderOccurrence.Create(_reminderAtUtc.Value);
            OnPropertyChanged(nameof(HasActiveReminder));
        }
    }

    public ChecklistReminderOccurrence? ReminderOccurrence
    {
        get => _reminderOccurrence;
        internal set
        {
            var occurrence = !_isChecked && _reminderAtUtc.HasValue ? value : null;
            if (SetProperty(ref _reminderOccurrence, occurrence))
                OnPropertyChanged(nameof(HasActiveReminder));
        }
    }

    internal ChecklistReminderRepeat? ReminderRepeat
    {
        get => _reminderRepeat;
        set
        {
            if (!SetProperty(ref _reminderRepeat, value)) return;
            OnPropertyChanged(nameof(HasActiveReminder));
            OnPropertyChanged(nameof(NextRepeatAtUtc));
        }
    }

    public DateTimeOffset? NextRepeatAtUtc => ReminderRepeat?.LastSlotUtc is not null ? ReminderRepeat.NextSlotUtc : null;

    public bool HasActiveReminder => !_isChecked && _reminderAtUtc.HasValue &&
        (NotificationStyle == ChecklistNotificationStyle.Urgent
            ? ReminderOccurrence is { AcknowledgedAtUtc: null } || ReminderRepeat?.NextSlotUtc is not null
            : _reminderHandledAtUtc is null || ReminderRepeat?.NextSlotUtc is not null);

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
