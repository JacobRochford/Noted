namespace Noted.Models;

public sealed class NoteDeleteRequestedEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}
