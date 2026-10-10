namespace Noted.Models;

public sealed record JotsWindowState
{
    public double Left { get; init; } = 240;
    public double Top { get; init; } = 170;
    public double Width { get; init; } = 430;
    public double Height { get; init; } = 570;
    public double Opacity { get; init; } = 0.88;
    public double GhostModeOpacity { get; init; } = 0.25;
    public bool GhostModeEnabled { get; init; }
    public bool ReopenOnStartup { get; init; }
}
