using System.Globalization;

namespace Noted.Helpers;

internal static class NoteSidebarLabel
{
    private const int MaximumScan = 2048;
    private const int MaximumLabel = 120;

    internal static string GetContentLabel(string content, string? initialContent)
    {
        var remaining = content.AsSpan(0, Math.Min(content.Length, MaximumScan));
        var header = ReadOnlySpan<char>.Empty;
        if (initialContent is not null && initialContent.Length <= MaximumScan)
        {
            var initial = initialContent.AsSpan().Trim();
            if (DateTime.TryParseExact(initial, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) header = initial;
        }
        var firstLine = true;
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOfAny('\r', '\n');
            var rawLine = end < 0 ? remaining : remaining[..end];
            var line = rawLine.Trim();
            remaining = end < 0 ? [] : remaining[(end + 1)..];
            if (line.IsEmpty) continue;
            if (firstLine && !header.IsEmpty && rawLine.SequenceEqual(header))
            {
                firstLine = false;
                continue;
            }
            return line[..Math.Min(line.Length, MaximumLabel)].ToString();
        }
        return string.Empty;
    }
}
