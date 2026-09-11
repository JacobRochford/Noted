using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class TextSearchSessionTests
{
    private readonly TextSearchSession _session = new(new TextSearchService());

    [TestMethod]
    public void Refresh_ResolvesCurrentMatchFromEditorAnchor()
    {
        Refresh("foo xx foo xx foo", "foo", anchor: 5);

        Assert.AreEqual(new TextSearchMatch(7, 3), _session.CurrentMatch);
        Assert.AreEqual(1, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void Next_AdvancesAndWrapsForward()
    {
        Refresh("foo xx foo xx foo", "foo", anchor: 0);

        Assert.AreEqual(new TextSearchMatch(7, 3), _session.Next());
        Assert.AreEqual(new TextSearchMatch(14, 3), _session.Next());
        Assert.AreEqual(new TextSearchMatch(0, 3), _session.Next());
    }

    [TestMethod]
    public void Previous_AdvancesAndWrapsBackward()
    {
        Refresh("foo xx foo xx foo", "foo", anchor: 14);

        Assert.AreEqual(new TextSearchMatch(7, 3), _session.Previous());
        Assert.AreEqual(new TextSearchMatch(0, 3), _session.Previous());
        Assert.AreEqual(new TextSearchMatch(14, 3), _session.Previous());
    }

    [TestMethod]
    public void Navigation_WrapsSingleMatch()
    {
        Refresh("xx foo xx", "foo", anchor: 0);

        Assert.AreEqual(new TextSearchMatch(3, 3), _session.Next());
        Assert.AreEqual(new TextSearchMatch(3, 3), _session.Previous());
    }

    [TestMethod]
    public void ManualCaretMovement_ReanchorsNext()
    {
        Refresh(FifteenMatches, "foo", anchor: 0);
        NavigateNext(6);

        var result = _session.Next(BetweenMatchesElevenAndTwelve);

        Assert.AreEqual(new TextSearchMatch(44, 3), result);
        Assert.AreEqual(11, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void ManualCaretMovement_ReanchorsPrevious()
    {
        Refresh(FifteenMatches, "foo", anchor: 0);
        NavigateNext(6);

        var result = _session.Previous(BetweenMatchesElevenAndTwelve);

        Assert.AreEqual(new TextSearchMatch(40, 3), result);
        Assert.AreEqual(10, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void ManualCaretMovement_BeforeFirstMatchFindsFirst()
    {
        Refresh("xx foo xx foo", "foo", anchor: 0);
        _session.Next();

        var result = _session.Next(0);

        Assert.AreEqual(new TextSearchMatch(3, 3), result);
    }

    [TestMethod]
    public void ManualCaretMovement_AfterLastMatchWrapsToFirst()
    {
        Refresh("foo xx foo", "foo", anchor: 0);

        var result = _session.Next(10);

        Assert.AreEqual(new TextSearchMatch(0, 3), result);
    }

    [TestMethod]
    public void ManualCaretMovement_DoesNotUseStaleNumericalIndex()
    {
        Refresh(FifteenMatches, "foo", anchor: 0);
        NavigateNext(6);
        Assert.AreEqual(6, _session.CurrentMatchIndex);

        var result = _session.Next(BetweenMatchesElevenAndTwelve);

        Assert.AreEqual(new TextSearchMatch(44, 3), result);
    }

    [TestMethod]
    public void QueryChange_ResolvesFromCurrentEditorAnchor()
    {
        Refresh("foo bar foo bar", "foo", anchor: 0);

        Refresh("foo bar foo bar", "bar", anchor: 9);

        Assert.AreEqual(new TextSearchMatch(12, 3), _session.CurrentMatch);
        Assert.AreEqual("bar", _session.Query);
    }

    [TestMethod]
    public void OptionChange_ResolvesFromCurrentEditorAnchor()
    {
        Refresh("FOO foo FOO", "foo", anchor: 0);
        var matchCase = new TextSearchOptions { MatchCase = true };

        _session.Refresh("FOO foo FOO", "document", "foo", matchCase, navigationAnchor: 5);

        Assert.AreEqual(new TextSearchMatch(4, 3), _session.CurrentMatch);
        Assert.AreEqual(matchCase, _session.Options);
    }

    [TestMethod]
    public void Reset_ClearsTransientState()
    {
        Refresh("foo foo", "foo", anchor: 0);

        _session.Reset();

        Assert.AreEqual(string.Empty, _session.Query);
        Assert.AreEqual(string.Empty, _session.SourceSnapshot);
        Assert.IsNull(_session.SourceIdentity);
        Assert.AreEqual(0, _session.Matches.Count);
        Assert.IsNull(_session.CurrentMatch);
        Assert.AreEqual(-1, _session.CurrentMatchIndex);
        Assert.AreEqual(0, _session.PreferredNavigationPosition);
    }

    [TestMethod]
    public void EditBeforeCurrentMatch_AdjustsLogicalMatchByDelta()
    {
        Refresh("foo xx foo", "foo", anchor: 7);

        Refresh("long foo xx foo", "foo", anchor: 5);

        Assert.AreEqual(new TextSearchMatch(12, 3), _session.CurrentMatch);
        Assert.AreEqual(1, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void EditInsideCurrentMatch_InvalidatesAndResolvesFromEditorAnchor()
    {
        Refresh("foo xx foo xx foo", "foo", anchor: 7);

        Refresh("foo xx fXo xx foo", "foo", anchor: 9);

        Assert.AreEqual(new TextSearchMatch(14, 3), _session.CurrentMatch);
        Assert.AreEqual(1, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void EditAfterCurrentMatch_RetainsLogicalMatch()
    {
        Refresh("foo xx foo", "foo", anchor: 0);

        Refresh("foo changed xx foo", "foo", anchor: 18);

        Assert.AreEqual(new TextSearchMatch(0, 3), _session.CurrentMatch);
        Assert.AreEqual(0, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void CurrentMatchRemoved_ResolvesFromEditorAnchor()
    {
        Refresh("foo xx foo xx foo", "foo", anchor: 7);

        Refresh("foo xx gone xx foo", "foo", anchor: 11);

        Assert.AreEqual(new TextSearchMatch(15, 3), _session.CurrentMatch);
        Assert.AreEqual(1, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void DifferentSourceIdentity_ResolvesFromEditorAnchorEvenWhenTextIsUnchanged()
    {
        Refresh("foo xx foo", "foo", anchor: 0, sourceIdentity: "document-a");

        Refresh("foo xx foo", "foo", anchor: 6, sourceIdentity: "document-b");

        Assert.AreEqual(new TextSearchMatch(7, 3), _session.CurrentMatch);
        Assert.AreEqual("document-b", _session.SourceIdentity);
    }

    [TestMethod]
    public void RefreshWithNoMatches_ClearsCurrentMatch()
    {
        Refresh("foo", "missing", anchor: 0);

        Assert.AreEqual(0, _session.Matches.Count);
        Assert.IsNull(_session.CurrentMatch);
        Assert.AreEqual(-1, _session.CurrentMatchIndex);
    }

    private const string FifteenMatches =
        "foo foo foo foo foo foo foo foo foo foo foo foo foo foo foo";

    private const int BetweenMatchesElevenAndTwelve = 43;

    private void Refresh(
        string text,
        string query,
        int anchor,
        object? sourceIdentity = null)
    {
        _session.Refresh(
            text,
            sourceIdentity ?? "document",
            query,
            default,
            anchor);
    }

    private void NavigateNext(int count)
    {
        for (var index = 0; index < count; index++)
            _session.Next();
    }
}
