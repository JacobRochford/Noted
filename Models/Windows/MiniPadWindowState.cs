namespace Noted.Models;

public sealed record MiniPadWindowState
{
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double Width { get; init; } = 320;
    public double Height { get; init; } = 240;
    public bool IsClosed { get; init; }
    public bool IsMaximized { get; init; }
    public IndentationSettings? Indentation { get; init; }
    public bool WordWrapEnabled { get; init; } = true;
    public bool ReopenOnStartup { get; init; }
}
