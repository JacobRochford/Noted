using System.Globalization;
using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class TextSearchServiceTests
{
    private readonly TextSearchService _service = new();

    [TestMethod]
    [DataRow("", "text")]
    [DataRow("text", "")]
    [DataRow("text", "longer")]
    [DataRow("text", "missing")]
    public void FindMatches_ReturnsNoMatchesWhenQueryCannotMatch(string text, string query)
    {
        var matches = _service.FindMatches(text, query, default);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void FindMatches_FindsOneMatch()
    {
        var matches = _service.FindMatches("before target after", "target", default);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(7, 6) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_FindsMultipleMatchesWithoutCase()
    {
        var matches = _service.FindMatches("Alpha alpha ALPHA", "alpha", default);

        CollectionAssert.AreEqual(
            new[]
            {
                new TextSearchMatch(0, 5),
                new TextSearchMatch(6, 5),
                new TextSearchMatch(12, 5)
            },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_RespectsMatchCase()
    {
        var options = new TextSearchOptions { MatchCase = true };

        var matches = _service.FindMatches("Alpha alpha ALPHA", "alpha", options);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(6, 5) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_ReturnsIntentionalNonOverlappingMatches()
    {
        var matches = _service.FindMatches("aaaa", "aa", default);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(0, 2), new TextSearchMatch(2, 2) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_UsesUtf16OffsetsForSurrogatePairs()
    {
        var matches = _service.FindMatches("😀 foo 😀foo", "foo", default);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(3, 3), new TextSearchMatch(9, 3) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_DoesNotNormalizeCombiningCharacters()
    {
        var text = "caf\u00e9 cafe\u0301";

        var composedMatches = _service.FindMatches(text, "\u00e9", default);
        var decomposedMatches = _service.FindMatches(text, "e\u0301", default);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(3, 1) },
            composedMatches.ToArray());
        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(8, 2) },
            decomposedMatches.ToArray());
    }

    [TestMethod]
    public void FindMatches_IsCultureIndependent()
    {
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            var matches = _service.FindMatches("I ı i İ", "i", default);

            CollectionAssert.AreEqual(
                new[] { new TextSearchMatch(0, 1), new TextSearchMatch(4, 1) },
                matches.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    public void FindNextMatch_ResolvesPositionsAndWraps()
    {
        var matches = MatchesAt(2, 8, 14);

        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindNextMatch(matches, 0));
        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindNextMatch(matches, 2));
        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindNextMatch(matches, 3));
        Assert.AreEqual(new TextSearchMatch(8, 3), _service.FindNextMatch(matches, 5));
        Assert.AreEqual(new TextSearchMatch(8, 3), _service.FindNextMatch(matches, 6));
        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindNextMatch(matches, 18));
    }

    [TestMethod]
    public void FindPreviousMatch_ResolvesPositionsAndWraps()
    {
        var matches = MatchesAt(2, 8, 14);

        Assert.AreEqual(new TextSearchMatch(14, 3), _service.FindPreviousMatch(matches, 0));
        Assert.AreEqual(new TextSearchMatch(14, 3), _service.FindPreviousMatch(matches, 2));
        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindPreviousMatch(matches, 3));
        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindPreviousMatch(matches, 5));
        Assert.AreEqual(new TextSearchMatch(2, 3), _service.FindPreviousMatch(matches, 7));
        Assert.AreEqual(new TextSearchMatch(14, 3), _service.FindPreviousMatch(matches, 20));
    }

    [TestMethod]
    public void Navigation_DoesNotWrapWhenDisabled()
    {
        var matches = MatchesAt(2, 8);

        Assert.IsNull(_service.FindNextMatch(matches, 20, wrapAround: false));
        Assert.IsNull(_service.FindPreviousMatch(matches, 0, wrapAround: false));
    }

    [TestMethod]
    public void Navigation_WrapsSingleMatch()
    {
        var matches = MatchesAt(5);

        Assert.AreEqual(new TextSearchMatch(5, 3), _service.FindNextMatch(matches, 8));
        Assert.AreEqual(new TextSearchMatch(5, 3), _service.FindPreviousMatch(matches, 5));
    }

    private static IReadOnlyList<TextSearchMatch> MatchesAt(params int[] starts) =>
        starts.Select(start => new TextSearchMatch(start, 3)).ToArray();
}
