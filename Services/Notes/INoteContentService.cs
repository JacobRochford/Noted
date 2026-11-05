using Noted.Models;

namespace Noted.Services;

// null ExistingContent means the destination was absent when the filename was accepted.
public sealed record NoteSaveDestination(string FilePath, TextFileContent? ExistingContent);

public interface INoteContentService
{
    string Load(string filePath);
    TextFileContent LoadWithEncoding(string filePath);

    string? Save(string filePath, string content, TextFileEncoding? encoding = null, TextFileContent? expectedVersion = null);

    string? SaveAs(string filePath, string content, TextFileEncoding? encoding = null,
        NoteSaveDestination? destinationState = null);

    string? SaveNewNoteAs(string initialPath, string destinationPath, string content, string initialContent,
        TextFileEncoding? encoding = null, NoteSaveDestination? destinationState = null);
}
