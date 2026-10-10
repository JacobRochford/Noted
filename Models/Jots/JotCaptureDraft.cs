namespace Noted.Models;

public sealed record JotCaptureDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = "";
}
