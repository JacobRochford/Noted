namespace Noted.Models;

public sealed record ChecklistItemState
{
    public Guid Id { get; init; }
    public string? Text { get; init; }
    public bool IsChecked { get; init; }
    public ChecklistPriority Priority { get; init; } = ChecklistPriority.None;
    public string? TabId { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTimeOffset? ReminderAtUtc { get; init; }
    public DateTimeOffset? ReminderHandledAtUtc { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}
