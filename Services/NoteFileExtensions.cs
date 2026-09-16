using System.IO;

namespace Noted.Services;

internal static class NoteFileExtensions
{
    internal static readonly IReadOnlyList<string> SupportedExtensions =
    [
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".sql", ".cs",
        ".xaml", ".html", ".htm", ".css", ".js", ".ts", ".py", ".ps1",
        ".bat", ".cmd"
    ];

    private static readonly string s_supportedPattern =
        string.Join(';', SupportedExtensions.Select(extension => $"*{extension}"));

    internal static string FileDialogFilter =>
        $"Supported text files ({s_supportedPattern})|{s_supportedPattern}";

    internal static bool IsSupported(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
