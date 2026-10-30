using System.Text.Json.Serialization;

namespace Noted.Models;

public sealed record JotCaptureDraft
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public string Text { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";

    [JsonIgnore]
    public bool HasContent => Text.Length > 0 || Title.Length > 0 || Category.Length > 0;
}
