namespace Noted.Services;

public interface INoteContentService
{
    string Load(string filePath);

    string? Save(string filePath, string content);
}
