namespace Noted.Search;

internal readonly record struct TextSearchOptions
{
    internal bool MatchCase { get; init; }
}
