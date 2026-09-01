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
    [DataRow("", "text")]
    [DataRow("text", "")]
    [DataRow("text", "missing")]
    public void FindMatches_ReturnsNoMatchesWhenSearchCannotRun(string text, string searchText)
    {
        var matches = PlainTextSearch.FindMatches(text, searchText, matchCase: false);

        Assert.AreEqual(0, matches.Count);
    }
}
