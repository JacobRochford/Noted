namespace Noted.Search;

internal sealed class TextSearchService
{
    internal IReadOnlyList<TextSearchMatch> FindMatches(
        string text,
        string query,
        TextSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(query);

        if (text.Length == 0 || query.Length == 0 || query.Length > text.Length)
            return [];

        var comparison = options.MatchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var matches = new List<TextSearchMatch>();
        var searchStart = 0;

        while (searchStart <= text.Length - query.Length)
        {
            var matchStart = text.IndexOf(query, searchStart, comparison);
            if (matchStart < 0)
                break;

            matches.Add(new TextSearchMatch(matchStart, query.Length));
            searchStart = matchStart + query.Length;
        }

        return matches;
    }

    internal TextSearchMatch? FindNextMatch(
        IReadOnlyList<TextSearchMatch> matches,
        int position,
        bool wrapAround = true)
    {
        ArgumentNullException.ThrowIfNull(matches);

        foreach (var match in matches)
        {
            if (match.End > position)
                return match;
        }

        return wrapAround && matches.Count > 0 ? matches[0] : null;
    }

    internal TextSearchMatch? FindPreviousMatch(
        IReadOnlyList<TextSearchMatch> matches,
        int position,
        bool wrapAround = true)
    {
        ArgumentNullException.ThrowIfNull(matches);

        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            if (match.Start < position)
                return match;
        }

        return wrapAround && matches.Count > 0 ? matches[^1] : null;
    }
}
