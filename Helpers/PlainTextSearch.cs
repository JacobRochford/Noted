namespace Noted.Helpers;

internal readonly record struct TextMatch(int Start, int Length);

internal static class PlainTextSearch
{
    internal static IReadOnlyList<TextMatch> FindMatches(
        string text,
        string searchText,
        bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(searchText))
            return [];

        var comparison = matchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TextMatch>();
        var searchStart = 0;

        while (searchStart <= text.Length - searchText.Length)
        {
            var matchStart = text.IndexOf(searchText, searchStart, comparison);
            if (matchStart < 0)
                break;

            matches.Add(new TextMatch(matchStart, searchText.Length));
            searchStart = matchStart + searchText.Length;
        }

        return matches;
    }
}
