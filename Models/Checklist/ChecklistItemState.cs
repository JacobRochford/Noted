namespace Noted.Models;

public sealed record ChecklistItemState
{
    public Guid Id { get; init; }
    public string? Text { get; init; }
    public bool IsChecked { get; init; }
    public ChecklistPriority Priority { get; init; } = ChecklistPriority.None;
    public string? TabId { get; init; }
    public DateTime? DueDate { get; init; }
    public TimeSpan? DueTime { get; init; }
    public DateTimeOffset? ReminderAtUtc { get; init; }
    public bool ReminderAtDueTime { get; init; }
    public DateTimeOffset? ReminderHandledAtUtc { get; init; }
    public ChecklistNotificationOptions? NotificationOptions { get; init; }
    public ChecklistNotificationStyle NotificationStyle { get; init; }
    public ChecklistReminderOccurrence? ReminderOccurrence { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}
