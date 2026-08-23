namespace Noted.Models;

public sealed record ChecklistTabState
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public ChecklistTabKind Kind { get; init; }
    public bool IsVisible { get; init; } = true;
    public int Order { get; init; }
}
