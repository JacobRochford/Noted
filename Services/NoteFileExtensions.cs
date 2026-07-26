using System.IO;

namespace Noted.Services;

internal static class NoteFileExtensions
{
    internal static readonly string[] Supported = [".txt", ".md", ".markdown"];

    internal static bool IsSupported(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return Supported.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
