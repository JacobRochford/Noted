namespace Noted.Search;

internal static class FindSelectionSeed
{
    internal static string? GetQuery(string? selectedText, bool isFindOpen, bool isEditorFocused)
    {
        if (isFindOpen && !isEditorFocused)
            return null;
        return selectedText is { Length: > 0 and <= 200 } && selectedText.IndexOfAny(['\r', '\n']) < 0
            ? selectedText
            : null;
    }
}
