namespace Noted.Search;

internal readonly record struct TextSearchOptions
{
    internal bool MatchCase { get; init; }

    internal bool MatchWholeWord { get; init; }

    internal bool UseRegex { get; init; }
}
