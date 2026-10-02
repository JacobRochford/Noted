using Noted.Models;

namespace Noted.Services;

internal readonly record struct IndentationEdit(int Start, int Length, string Replacement);

internal static class Indentation
{
    internal static IndentationSettings Detect(string text, IndentationSettings current)
    {
        var spaces = new List<int>();
        var tabs = 0;
        var indentedLines = 0;
        foreach (var (start, length, blank) in GetPrefixes(text))
        {
            if (blank || length == 0)
                continue;

            indentedLines++;
            var prefix = text.AsSpan(start, length);
            if (!prefix.Contains('\t'))
                spaces.Add(length);
            else if (prefix[0] == '\t' && !prefix.TrimStart('\t').Contains('\t'))
                tabs++; // spaces after tabs still count as tab indentation
        }

        if (tabs >= 2 && tabs * 4 >= indentedLines * 3)
            return current with { Mode = IndentationMode.Tabs };
        if (spaces.Count < 2 || spaces.Count * 4 < indentedLines * 3)
            return current;

        // Make sure alignment spacing isn't mistaken for indentation.
        // Require repeated widths or consistent indentation steps.
        var size = spaces[0];
        foreach (var width in spaces)
        {
            var remainder = width;
            while (remainder != 0)
                (size, remainder) = (remainder, size % remainder);
        }

        var evidence = spaces.Count(width => width == size);
        for (var index = 1; index < spaces.Count; index++)
            if (Math.Abs(spaces[index] - spaces[index - 1]) == size)
                evidence++;

        return current with
        {
            Mode = IndentationMode.Spaces,
            TabSize = size is >= 2 and <= 8 && evidence >= 2 ? size : current.TabSize
        };
    }

    internal static IReadOnlyList<IndentationEdit> GetConversionEdits(
        string text, int tabSize, IndentationMode target)
    {
        var edits = new List<IndentationEdit>();
        foreach (var (start, length, _) in GetPrefixes(text))
        {
            if (length == 0)
                continue;

            var prefix = text.AsSpan(start, length);
            var column = GetColumn(prefix, tabSize);
            var replacement = target == IndentationMode.Spaces
                ? new string(' ', column)
                : new string('\t', column / tabSize) + new string(' ', column % tabSize);

            if (!prefix.SequenceEqual(replacement.AsSpan()))
                edits.Add(new(start, length, replacement));
        }

        return edits;
    }

    internal static int MapOffset(string text, IReadOnlyList<IndentationEdit> edits, int offset, int tabSize)
    {
        var delta = 0;
        foreach (var edit in edits)
        {
            if (offset < edit.Start)
                break;

            if (offset <= edit.Start + edit.Length)
            {
                var column = GetColumn(text.AsSpan(edit.Start, offset - edit.Start), tabSize);
                var replacementOffset = 0;
                var replacementColumn = 0;
                foreach (var character in edit.Replacement)
                {
                    var nextColumn = replacementColumn + (character == '\t'
                        ? tabSize - replacementColumn % tabSize : 1);

                    // can't place the caret inside a tab
                    if (nextColumn > column)
                        break;

                    replacementColumn = nextColumn;
                    replacementOffset++;
                }

                return edit.Start + delta + replacementOffset;
            }

            delta += edit.Replacement.Length - edit.Length;
        }

        return offset + delta;
    }

    private static IEnumerable<(int Start, int Length, bool Blank)> GetPrefixes(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] is ' ' or '\t')
                end++;

            yield return (start, end - start, end == text.Length || text[end] is '\r' or '\n');

            while (end < text.Length && text[end] is not ('\r' or '\n'))
                end++;

            if (end < text.Length && text[end++] == '\r' && end < text.Length && text[end] == '\n')
                end++;

            start = end;
        }
    }

    internal static int GetColumn(ReadOnlySpan<char> textBeforeInsertion, int tabSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tabSize, 1);
        var lineStart = textBeforeInsertion.LastIndexOfAny('\r', '\n') + 1;
        var column = 0;
        foreach (var character in textBeforeInsertion[lineStart..])
            column += character == '\t' ? tabSize - column % tabSize : 1;
        return column;
    }

    internal static string GetInsertion(ReadOnlySpan<char> textBeforeInsertion, IndentationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Mode == IndentationMode.Tabs
            ? "\t"
            : new string(' ', settings.TabSize - GetColumn(textBeforeInsertion, settings.TabSize) % settings.TabSize);
    }
}