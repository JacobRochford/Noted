namespace Noted.Models;

public sealed record ScratchpadWindowState
{
    public double Left { get; init; } = 150;
    public double Top { get; init; } = 150;
    public double Width { get; init; } = 450;
    public double Height { get; init; } = 500;
    public double Opacity { get; init; } = 0.88;
    public double GhostModeOpacity { get; init; } = 0.25;
    public bool GhostModeEnabled { get; init; }
    public bool WordWrapEnabled { get; init; } = true;
    public double FontSize { get; init; } = 13;
    public bool ReopenOnStartup { get; init; }
}
