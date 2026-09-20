namespace Noted.Models;

public sealed record DictionaryItemState
{
    public string? Word { get; init; }
    public string? Description { get; init; }
}
