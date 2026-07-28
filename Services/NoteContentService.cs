using System.IO;
using System.Text;

namespace Noted.Services;

public sealed class NoteContentService : INoteContentService
{
    private readonly Func<string> _notesDirectoryProvider;

    public NoteContentService(Func<string> notesDirectoryProvider)
    {
        ArgumentNullException.ThrowIfNull(notesDirectoryProvider);
        _notesDirectoryProvider = notesDirectoryProvider;
    }

    public string Load(string filePath)
    {
        var validatedPath = ValidateNotePath(filePath);
        return File.ReadAllText(validatedPath, Encoding.UTF8);
    }

    public void Save(string filePath, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validatedPath = ValidateNotePath(filePath);
        AtomicFileWriter.WriteAllText(validatedPath, content);
    }

    private string ValidateNotePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var notesDirectory = _notesDirectoryProvider();
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);

        var rootPath = Path.GetFullPath(notesDirectory);
        var fullPath = Path.GetFullPath(filePath);

        if (!NoteFileExtensions.IsSupported(fullPath))
            throw new ArgumentException(
                "Only .txt, .md, and .markdown note files can be opened or saved.",
                nameof(filePath));

        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The note file must be inside the configured notes directory.");
        }

        return fullPath;
    }
}
