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
        var directory = Path.GetDirectoryName(validatedPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(validatedPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(validatedPath))
                File.Replace(temporaryPath, validatedPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, validatedPath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
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

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
