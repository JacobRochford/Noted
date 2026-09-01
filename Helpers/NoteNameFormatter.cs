using System.Globalization;
using System.IO;

namespace Noted.Helpers;

internal static class NoteNameFormatter
{
    private const string GeneratedNameFormat = "yyyy-MM-dd_HH-mm-ss";

    // Timestamp recognition is for display only. UsesGeneratedName controls save behavior.
    internal static string Format(string fileName, bool keepExtensionForCustomName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (TryGetGeneratedDate(name, out var createdAt))
            return createdAt.ToString("MMM d, yyyy 'at' h:mm:ss tt", CultureInfo.InvariantCulture);

        return keepExtensionForCustomName
            ? Path.GetFileName(fileName)
            : name;
    }

    internal static bool IsGenerated(string fileName)
    {
        return TryGetGeneratedDate(Path.GetFileNameWithoutExtension(fileName), out _);
    }

    private static bool TryGetGeneratedDate(string name, out DateTime createdAt)
    {
        var timestampText = name.StartsWith("Note_", StringComparison.Ordinal)
            ? name[5..]
            : name;

        var underscoreIndex = timestampText.LastIndexOf('_');
        if (underscoreIndex > 0)
        {
            var suffix = timestampText[(underscoreIndex + 1)..];
            if ((suffix.Length == 2 && suffix.All(char.IsDigit))
                || (suffix.Length == 32 && suffix.All(Uri.IsHexDigit)))
            {
                timestampText = timestampText[..underscoreIndex];
            }
        }

        return DateTime.TryParseExact(
            timestampText,
            GeneratedNameFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out createdAt);
    }
}
