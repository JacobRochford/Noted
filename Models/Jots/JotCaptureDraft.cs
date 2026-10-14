using System.Text.Json.Serialization;

namespace Noted.Models;

public sealed record JotCaptureDraft
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public string Text { get; init; } = "";
}
