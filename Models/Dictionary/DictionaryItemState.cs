namespace Noted.Models;

public sealed record DictionaryItemState
{
    public Guid Id { get; init; }
    public string? Word { get; init; }
    public string? Description { get; init; }
}
