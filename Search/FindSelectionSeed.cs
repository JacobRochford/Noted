namespace Noted.Search;

internal static class FindSelectionSeed
{
    internal static string? GetQuery(string? selectedText, bool isFindOpen, bool isEditorFocused,
        string currentQuery, Func<string?> getCaretWord)
    {
        if (isFindOpen && !isEditorFocused)
            return null;
        if (!string.IsNullOrEmpty(selectedText))
            return selectedText.Length <= 200 && selectedText.IndexOfAny(['\r', '\n']) < 0
                ? selectedText
                : null;
        return currentQuery.Length == 0 ? getCaretWord() : null;
    }

    internal static string? GetWordAtCaret(string text, int caret)
    {
        var start = caret;
        var end = caret;
        while (start > 0 && TextSearchSession.IsWordCharacter(text[start - 1]))
            start--;
        while (end < text.Length && TextSearchSession.IsWordCharacter(text[end]))
            end++;
        return end > start && end - start <= 200 ? text[start..end] : null;
    }
}
