namespace Noted.Models;

public sealed record ChecklistWindowState
{
    public double Left { get; init; } = 100;
    public double Top { get; init; } = 100;
    public double Width { get; init; } = 300;
    public double Height { get; init; } = 400;
    public double Opacity { get; init; } = 0.88;
    public double GhostModeOpacity { get; init; } = 0.25;
    public bool GhostModeEnabled { get; init; } = false;
    public bool ReopenOnStartup { get; init; }
    public ChecklistPriorityColors PriorityColors { get; init; } = new();
}
