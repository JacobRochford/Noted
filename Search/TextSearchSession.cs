namespace Noted.Search;

internal sealed class TextSearchSession
{
    private readonly TextSearchService _searchService;

    internal TextSearchSession(TextSearchService searchService)
    {
        ArgumentNullException.ThrowIfNull(searchService);
        _searchService = searchService;
    }

    internal string Query { get; private set; } = string.Empty;

    internal TextSearchOptions Options { get; private set; }

    internal string SourceSnapshot { get; private set; } = string.Empty;

    internal object? SourceIdentity { get; private set; }

    internal IReadOnlyList<TextSearchMatch> Matches { get; private set; } = [];

    internal int CurrentMatchIndex { get; private set; } = -1;

    internal TextSearchMatch? CurrentMatch =>
        CurrentMatchIndex >= 0 ? Matches[CurrentMatchIndex] : null;

    internal int PreferredNavigationPosition { get; private set; }

    internal void Refresh(
        string sourceSnapshot,
        object? sourceIdentity,
        string query,
        TextSearchOptions options,
        int navigationAnchor)
    {
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        ArgumentNullException.ThrowIfNull(query);

        navigationAnchor = Math.Clamp(navigationAnchor, 0, sourceSnapshot.Length);

        var queryChanged = !string.Equals(Query, query, StringComparison.Ordinal);
        var optionsChanged = Options != options;
        var sourceChanged = !string.Equals(SourceSnapshot, sourceSnapshot, StringComparison.Ordinal);
        var identityChanged = !Equals(SourceIdentity, sourceIdentity);

        if (!queryChanged && !optionsChanged && !sourceChanged && !identityChanged)
            return;

        var previousSnapshot = SourceSnapshot;
        var previousCurrentMatch = CurrentMatch;
        var previousPreferredPosition = PreferredNavigationPosition;

        Query = query;
        Options = options;
        SourceSnapshot = sourceSnapshot;
        SourceIdentity = sourceIdentity;
        Matches = _searchService.FindMatches(sourceSnapshot, query, options);

        if (!queryChanged && !optionsChanged && !identityChanged && sourceChanged &&
            previousCurrentMatch is { } currentMatch &&
            TryPreserveCurrentMatch(
                previousSnapshot,
                sourceSnapshot,
                currentMatch,
                previousPreferredPosition))
        {
            return;
        }

        PreferredNavigationPosition = navigationAnchor;
        SetCurrent(_searchService.FindNextMatch(Matches, navigationAnchor));
    }

    internal TextSearchMatch? Next(int? navigationAnchor = null)
    {
        var anchor = navigationAnchor ?? CurrentMatch?.End ?? PreferredNavigationPosition;
        PreferredNavigationPosition = anchor;
        var match = _searchService.FindNextMatch(Matches, anchor);
        SetCurrent(match);
        return match;
    }

    internal TextSearchMatch? Previous(int? navigationAnchor = null)
    {
        var anchor = navigationAnchor ?? CurrentMatch?.Start ?? PreferredNavigationPosition;
        PreferredNavigationPosition = anchor;
        var match = _searchService.FindPreviousMatch(Matches, anchor);
        SetCurrent(match);
        return match;
    }

    internal void Reset()
    {
        Query = string.Empty;
        Options = default;
        SourceSnapshot = string.Empty;
        SourceIdentity = null;
        Matches = [];
        CurrentMatchIndex = -1;
        PreferredNavigationPosition = 0;
    }

    private bool TryPreserveCurrentMatch(
        string previousSnapshot,
        string sourceSnapshot,
        TextSearchMatch previousMatch,
        int previousPreferredPosition)
    {
        var change = FindChange(previousSnapshot, sourceSnapshot);
        int expectedStart;

        if (change.OldEnd <= previousMatch.Start)
        {
            expectedStart = previousMatch.Start + change.Delta;
            PreferredNavigationPosition = previousPreferredPosition >= change.OldEnd
                ? previousPreferredPosition + change.Delta
                : previousPreferredPosition;
        }
        else if (change.Start >= previousMatch.End)
        {
            expectedStart = previousMatch.Start;
            PreferredNavigationPosition = previousPreferredPosition;
        }
        else
        {
            return false;
        }

        var preservedIndex = FindMatchIndex(expectedStart, previousMatch.Length);
        if (preservedIndex < 0)
            return false;

        CurrentMatchIndex = preservedIndex;
        PreferredNavigationPosition = Math.Clamp(
            PreferredNavigationPosition,
            0,
            sourceSnapshot.Length);
        return true;
    }

    private void SetCurrent(TextSearchMatch? match)
    {
        CurrentMatchIndex = match is { } value
            ? FindMatchIndex(value.Start, value.Length)
            : -1;
    }

    private int FindMatchIndex(int start, int length)
    {
        for (var index = 0; index < Matches.Count; index++)
        {
            var match = Matches[index];
            if (match.Start == start && match.Length == length)
                return index;
        }

        return -1;
    }

    private static TextChange FindChange(string oldText, string newText)
    {
        var sharedPrefixLength = 0;
        var maximumPrefixLength = Math.Min(oldText.Length, newText.Length);

        while (sharedPrefixLength < maximumPrefixLength &&
               oldText[sharedPrefixLength] == newText[sharedPrefixLength])
        {
            sharedPrefixLength++;
        }

        var sharedSuffixLength = 0;
        var maximumSuffixLength = Math.Min(
            oldText.Length - sharedPrefixLength,
            newText.Length - sharedPrefixLength);

        while (sharedSuffixLength < maximumSuffixLength &&
               oldText[oldText.Length - sharedSuffixLength - 1] ==
               newText[newText.Length - sharedSuffixLength - 1])
        {
            sharedSuffixLength++;
        }

        var oldLength = oldText.Length - sharedPrefixLength - sharedSuffixLength;
        var newLength = newText.Length - sharedPrefixLength - sharedSuffixLength;
        return new TextChange(sharedPrefixLength, oldLength, newLength);
    }

    private readonly record struct TextChange(int Start, int OldLength, int NewLength)
    {
        internal int OldEnd => Start + OldLength;

        internal int Delta => NewLength - OldLength;
    }
}
