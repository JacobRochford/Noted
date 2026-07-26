namespace Noted.Services;

public interface INoteContentService
{
    string Load(string filePath);

    void Save(string filePath, string content);
}
