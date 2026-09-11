using System.Globalization;
using Noted.Helpers;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class PlainTextSearchTests
{
    [TestMethod]
    public void FindMatches_FindsTextWithoutMatchingCase()
    {
        var matches = PlainTextSearch.FindMatches("Alpha alpha ALPHA", "alpha", matchCase: false);

        CollectionAssert.AreEqual(
            new[] { new TextMatch(0, 5), new TextMatch(6, 5), new TextMatch(12, 5) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_RespectsMatchCase()
    {
        var matches = PlainTextSearch.FindMatches("Alpha alpha ALPHA", "alpha", matchCase: true);

        CollectionAssert.AreEqual(
            new[] { new TextMatch(6, 5) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_ReturnsNonOverlappingMatches()
    {
        var matches = PlainTextSearch.FindMatches("aaaa", "aa", matchCase: false);

        CollectionAssert.AreEqual(
            new[] { new TextMatch(0, 2), new TextMatch(2, 2) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_ReturnsNoMatchesForEmptyText()
    {
        var matches = PlainTextSearch.FindMatches(string.Empty, "text", matchCase: false);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void FindMatches_ReturnsNoMatchesForEmptyQuery()
    {
        var matches = PlainTextSearch.FindMatches("text", string.Empty, matchCase: false);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void FindMatches_ReturnsNoMatchesWhenQueryIsLongerThanText()
    {
        var matches = PlainTextSearch.FindMatches("text", "longer", matchCase: false);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void FindMatches_ReturnsNoMatchesWhenQueryIsMissing()
    {
        var matches = PlainTextSearch.FindMatches("text", "missing", matchCase: false);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void FindMatches_ReturnsOneMatch()
    {
        var matches = PlainTextSearch.FindMatches("before target after", "target", matchCase: false);

        CollectionAssert.AreEqual(new[] { new TextMatch(7, 6) }, matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_UsesUtf16OffsetsForSurrogatePairs()
    {
        var matches = PlainTextSearch.FindMatches("😀 foo 😀foo", "foo", matchCase: false);

        CollectionAssert.AreEqual(
            new[] { new TextMatch(3, 3), new TextMatch(9, 3) },
            matches.ToArray());
    }

    [TestMethod]
    public void FindMatches_DoesNotNormalizeCombiningCharacters()
    {
        var text = "caf\u00e9 cafe\u0301";

        var composedMatches = PlainTextSearch.FindMatches(text, "\u00e9", matchCase: false);
        var decomposedMatches = PlainTextSearch.FindMatches(text, "e\u0301", matchCase: false);

        CollectionAssert.AreEqual(new[] { new TextMatch(3, 1) }, composedMatches.ToArray());
        CollectionAssert.AreEqual(new[] { new TextMatch(8, 2) }, decomposedMatches.ToArray());
    }

    [TestMethod]
    public void FindMatches_IsCultureIndependent()
    {
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            var matches = PlainTextSearch.FindMatches("I ı i İ", "i", matchCase: false);

            CollectionAssert.AreEqual(
                new[] { new TextMatch(0, 1), new TextMatch(4, 1) },
                matches.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
