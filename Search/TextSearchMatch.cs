namespace Noted.Search;

internal readonly record struct TextSearchMatch(int Start, int Length)
{
    internal int End => Start + Length;
}
