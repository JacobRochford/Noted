namespace Noted.Models;

public sealed record MiniPadWindowState
{
    public bool WordWrapEnabled { get; init; } = true;
    public bool ReopenOnStartup { get; init; }
}
