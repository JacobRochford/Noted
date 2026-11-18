using System.Text.Json.Serialization;

namespace Noted.Models;

public sealed record DictionaryItemState
{
    public Guid Id { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SavedDraftRevisionId { get; init; }
    public string? Word { get; init; }
    public string? Description { get; init; }
}
