namespace Noted.Models;

public sealed record JotItemState
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? Title { get; init; }
    public string? Details { get; init; }
    public string? Category { get; init; }
    public bool IsPinned { get; init; }
    public bool IsStarred { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; init; } = DateTimeOffset.UtcNow;
}
