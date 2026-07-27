namespace Noted.Models;

public sealed record ArchivedNoteItem
{
    public string RelativePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
}
