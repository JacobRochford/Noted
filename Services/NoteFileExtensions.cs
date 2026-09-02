using System.IO;

namespace Noted.Services;

internal static class NoteFileExtensions
{
    internal static readonly string[] Supported =
    [
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".sql", ".cs",
        ".xaml", ".html", ".htm", ".css", ".js", ".ts", ".py", ".ps1",
        ".bat", ".cmd"
    ];

    private static readonly string SupportedPattern =
        string.Join(';', Supported.Select(extension => $"*{extension}"));

    internal static string FileDialogFilter =>
        $"Supported text files ({SupportedPattern})|{SupportedPattern}";

    internal static bool IsSupported(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return Supported.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
