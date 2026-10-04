using Noted.Models;

namespace Noted.Services;

public interface INoteContentService
{
    string Load(string filePath);
    TextFileContent LoadWithEncoding(string filePath);

    string? Save(string filePath, string content, TextFileEncoding? encoding = null);

    string? SaveAs(string filePath, string content, TextFileEncoding? encoding = null);

    string? SaveNewNoteAs(string initialPath, string destinationPath, string content, string initialContent, TextFileEncoding? encoding = null);
}
