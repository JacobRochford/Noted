using System.Text.Json;
using Noted.Models;

namespace Noted.Services;

internal readonly record struct JsonWhitespaceEdit(int Start, int Length, string Replacement);

internal static class JsonFormatting
{
    internal static bool TryGetEdits(string text, bool minify, IndentationSettings settings,
        string fallbackNewline, out IReadOnlyList<JsonWhitespaceEdit> edits, out string? error)
    {
        edits = [];
        error = null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON near line {(ex.LineNumber ?? 0) + 1}, byte position {(ex.BytePositionInLine ?? 0) + 1}. Text was not changed.";
            return false;
        }

        // Keep every token verbatim, including duplicate properties, number spellings and escapes.
        // The parser owns validation; this scan only locates whitespace between valid tokens.
        var tokens = new List<(int Start, int Length)>();
        var index = 0;
        while (index < text.Length)
        {
            if (IsWhitespace(text[index])) { index++; continue; }
            var start = index++;
            if (text[start] == '"')
            {
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character == '\\') index++;
                    else if (character == '"') break;
                }
            }
            else if (text[start] is not ('{' or '}' or '[' or ']' or ':' or ','))
            {
                while (index < text.Length && !IsWhitespace(text[index]) &&
                    text[index] is not ('}' or ']' or ',')) index++;
            }
            tokens.Add((start, index - start));
        }

        var newline = GetNewline(text, fallbackNewline);
        var indentation = settings.Mode == IndentationMode.Tabs ? "\t" : new string(' ', settings.TabSize);
        var changes = new List<JsonWhitespaceEdit>();
        var depth = 0;
        var previousEnd = 0;
        var previous = '\0';
        foreach (var token in tokens)
        {
            var current = text[token.Start];
            if (current is '}' or ']') depth--;
            var whitespace = "";
            if (!minify)
            {
                var empty = (previous == '{' && current == '}') || (previous == '[' && current == ']');
                if (!empty && (previous is '{' or '[' or ',' || current is '}' or ']'))
                    whitespace = newline + string.Concat(Enumerable.Repeat(indentation, depth));
                else if (previous == ':') whitespace = " ";
            }
            AddGap(previousEnd, token.Start, whitespace);
            previousEnd = token.Start + token.Length;
            previous = current;
            if (current is '{' or '[') depth++;
        }
        // Preserve a document's terminal newline when pretty-printing.
        var trailing = !minify && text.AsSpan(previousEnd).IndexOfAny('\r', '\n') >= 0 ? newline : "";
        AddGap(previousEnd, text.Length, trailing);
        edits = changes;
        return true;

        void AddGap(int start, int end, string replacement)
        {
            if (!text.AsSpan(start, end - start).SequenceEqual(replacement.AsSpan()))
                changes.Add(new(start, end - start, replacement));
        }
    }

    internal static string GetNewline(string text, string fallback)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? fallback : text[index] == '\r'
            ? index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r" : "\n";
    }

    internal static int MapOffset(IReadOnlyList<JsonWhitespaceEdit> edits, int offset)
    {
        var delta = 0;
        foreach (var edit in edits)
        {
            if (offset < edit.Start) break;
            if (offset <= edit.Start + edit.Length)
                return edit.Start + delta + (offset == edit.Start + edit.Length
                    ? edit.Replacement.Length : Math.Min(offset - edit.Start, edit.Replacement.Length));
            delta += edit.Replacement.Length - edit.Length;
        }
        return offset + delta;
    }

    private static bool IsWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';
}
