namespace Noted.Models;

public sealed record DictionaryWindowState
{
    public double Left { get; init; } = 200;
    public double Top { get; init; } = 150;
    public double Width { get; init; } = 400;
    public double Height { get; init; } = 500;
    public double Opacity { get; init; } = 0.88;
    public double GhostModeOpacity { get; init; } = 0.25;
    public bool GhostModeEnabled { get; init; } = false;
    public bool ReopenOnStartup { get; init; }
}
