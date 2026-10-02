namespace Noted.Models;

public sealed record MiniPadWindowState
{
    public IndentationSettings? Indentation { get; init; }
    public bool WordWrapEnabled { get; init; } = true;
    public bool ReopenOnStartup { get; init; }
}
