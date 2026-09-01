namespace Noted.Models;

public sealed class NoteRenameRequestedEventArgs(
    string filePath,
    bool isFirstSave) : EventArgs
{
    public string FilePath { get; } = filePath;
    public bool IsFirstSave { get; } = isFirstSave;
    public string? NewFilePath { get; set; }
    public bool IsCanceled { get; set; }
}
