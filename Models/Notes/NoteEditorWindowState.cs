namespace Noted.Models;

public sealed record NoteEditorWindowState
{
    public double Width { get; init; } = 900;
    public double Height { get; init; } = 650;
    public double TabsPanelWidth { get; init; } = 190;
    public bool IsTabsPanelCollapsed { get; init; }
    public bool WordWrapEnabled { get; init; } = true;
    public bool ShowLineNumbers { get; init; } = true;
    public int ScrollSpeed { get; init; } = 2;
    public bool ReopenOnStartup { get; init; }
}
