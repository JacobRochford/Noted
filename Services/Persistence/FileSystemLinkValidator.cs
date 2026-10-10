using System.IO;

namespace Noted.Services;

// recheck paths before each access, but another process can still replace them
// between the check and the file operation.
internal static class FileSystemLinkValidator
{
    internal static FileAttributes? GetAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Entry '{path}' is a filesystem link and cannot be accessed safely.");
            return attributes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void ValidatePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is not null)
            ValidateDirectory(parent);
        GetAttributes(fullPath);
    }

    internal static void ValidateDirectory(string path)
    {
        ValidatePath(path);
        var attributes = GetAttributes(path);
        if (attributes is not null && (attributes.Value & FileAttributes.Directory) == 0)
            throw new IOException($"Folder '{path}' is occupied by a file.");
    }
}
