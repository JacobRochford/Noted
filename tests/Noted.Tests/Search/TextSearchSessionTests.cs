using System.Globalization;
using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class TextSearchSessionTests
{
    private readonly TextSearchSession _session = new();

    [TestMethod]
    [DataRow("", "text")]
    [DataRow("text", "")]
    [DataRow("text", "longer")]
    [DataRow("text", "missing")]
    public void Refresh_ReturnsNoMatchesWhenQueryCannotMatch(string text, string query)
    {
        Refresh(text, query, anchor: 0);

        Assert.AreEqual(0, _session.Matches.Count);
    }

    [TestMethod]
    public void Refresh_FindsCaseInsensitiveNonOverlappingMatches()
    {
        Refresh("Alpha alpha ALPHA aaaa", "alpha", anchor: 0);

        CollectionAssert.AreEqual(
            new[]
            {
                new TextSearchMatch(0, 5),
                new TextSearchMatch(6, 5),
                new TextSearchMatch(12, 5)
            },
            _session.Matches.ToArray());

        Refresh("aaaa", "aa", anchor: 0);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(0, 2), new TextSearchMatch(2, 2) },
            _session.Matches.ToArray());
    }

    [TestMethod]
    public void Refresh_UsesUtf16OffsetsWithoutNormalizingText()
    {
        Refresh("😀 foo 😀foo", "foo", anchor: 0);

        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(3, 3), new TextSearchMatch(9, 3) },
            _session.Matches.ToArray());

        var text = "caf\u00e9 cafe\u0301";
        Refresh(text, "\u00e9", anchor: 0);
        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(3, 1) },
            _session.Matches.ToArray());

        Refresh(text, "e\u0301", anchor: 0);
        CollectionAssert.AreEqual(
            new[] { new TextSearchMatch(8, 2) },
            _session.Matches.ToArray());
    }

    [TestMethod]
    public void Refresh_IsCultureIndependent()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            Refresh("I ı i İ", "i", anchor: 0);

            CollectionAssert.AreEqual(
                new[] { new TextSearchMatch(0, 1), new TextSearchMatch(4, 1) },
                _session.Matches.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

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
    public void QueryShortening_ResolvesEachQueryFromSuppliedAnchor()
    {
        const string text = "den early dental later";
        var dentalStart = text.IndexOf("dental", StringComparison.Ordinal);
        Refresh(text, "dental", anchor: dentalStart - 1);
        Assert.AreEqual(new TextSearchMatch(dentalStart, 6), _session.CurrentMatch);

        Refresh(text, "denta", anchor: 0);
        Assert.AreEqual(new TextSearchMatch(dentalStart, 5), _session.CurrentMatch);

        Refresh(text, "dent", anchor: 0);
        Assert.AreEqual(new TextSearchMatch(dentalStart, 4), _session.CurrentMatch);

        Refresh(text, "den", anchor: 0);
        Assert.AreEqual(new TextSearchMatch(0, 3), _session.CurrentMatch);
    }

    [TestMethod]
    public void QueryShortening_ResolvesEarlierNewMatchFromSuppliedAnchor()
    {
        const string text = "den early dental later den";
        var dentalStart = text.IndexOf("dental", StringComparison.Ordinal);
        Refresh(text, "dental", anchor: dentalStart - 1);

        Refresh(text, "den", anchor: 0);

        Assert.AreEqual(new TextSearchMatch(0, 3), _session.CurrentMatch);
        Assert.AreEqual(0, _session.CurrentMatchIndex);
    }

    [TestMethod]
    public void QueryEditing_DoesNotWrapToLastMatchWhenCurrentOccurrenceStillMatches()
    {
        const string text = "dental middle den";
        Refresh(text, "dental", anchor: 0);
        Assert.AreEqual(new TextSearchMatch(0, 6), _session.CurrentMatch);

        Refresh(text, "den", anchor: text.Length);

        Assert.AreEqual(new TextSearchMatch(0, 3), _session.CurrentMatch);
        Assert.AreEqual(0, _session.CurrentMatchIndex);
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
    public void QueryOrOptionsChange_NotifiesAfterEstablishingCurrentResult()
    {
        Refresh("foo xx foo xx foo", "foo", anchor: 0);
        var notifications = 0;
        _session.QueryOrOptionsChanged += () =>
        {
            notifications++;
            Assert.AreEqual(1, _session.CurrentMatchIndex);
            Assert.AreEqual("fo", _session.Query);
        };

        Refresh("foo xx foo xx foo", "fo", anchor: 8);
        Assert.AreEqual(1, notifications);
        _session.Refresh("foo xx foo xx foo", "document", "fo",
            new TextSearchOptions { MatchCase = true }, navigationAnchor: 8);
        Assert.AreEqual(2, notifications);
    }

    [TestMethod]
    public void UnchangedQueryAndOptions_DoNotNotifyForRefreshOrDocumentChanges()
    {
        Refresh("foo xx foo", "foo", anchor: 0);
        var notifications = 0;
        _session.QueryOrOptionsChanged += () => notifications++;

        Refresh("foo xx foo", "foo", anchor: 8);
        Refresh("foo xx foo!", "foo", anchor: 8);

        Assert.AreEqual(0, notifications);
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

    [TestMethod]
    public void LiteralMode_PreservesMetacharactersAndNonOverlappingRanges()
    {
        Refresh("a.* A.* axyz", "a.*", anchor: 0);
        CollectionAssert.AreEqual(new[] { new TextSearchMatch(0, 3), new TextSearchMatch(4, 3) },
            _session.Matches.ToArray());
        Assert.IsNull(_session.ValidationMessage);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WholeWord_UsesUnicodeWordCharactersAndTextEdges(bool regex)
    {
        const string text = "cat cats _cat cat_ cat2 écat cat\u0301 cat-cat (cat) cat";
        _session.Refresh(text, "document", "cat",
            new TextSearchOptions { MatchWholeWord = true, UseRegex = regex }, 0);
        CollectionAssert.AreEqual(new[] { 0, 34, 38, 43, 48 },
            _session.Matches.Select(match => match.Start).ToArray());
    }

    [TestMethod]
    [DataRow(false, false, 2)]
    [DataRow(false, true, 1)]
    [DataRow(true, false, 2)]
    [DataRow(true, true, 1)]
    public void WholeWord_ComposesWithCaseSensitivity(bool regex, bool matchCase, int count)
    {
        _session.Refresh("CAT cat cats", "document", "cat",
            new TextSearchOptions { MatchWholeWord = true, UseRegex = regex, MatchCase = matchCase }, 0);
        Assert.AreEqual(count, _session.Matches.Count);
    }

    [TestMethod]
    public void Regex_VariableLengthMatchesNavigateAndWrapAtBoundaries()
    {
        _session.Refresh("a12 b345", "document", @"\d+",
            new TextSearchOptions { UseRegex = true }, 2);
        Assert.AreEqual(new TextSearchMatch(1, 2), _session.CurrentMatch);
        Assert.AreEqual(new TextSearchMatch(5, 3), _session.Next());
        Assert.AreEqual(new TextSearchMatch(1, 2), _session.Next());
        Assert.AreEqual(new TextSearchMatch(5, 3), _session.Previous());
        Assert.AreEqual(new TextSearchMatch(1, 2), _session.Next(8));
        Assert.AreEqual(new TextSearchMatch(5, 3), _session.Previous(0));
    }

    [TestMethod]
    public void Regex_InlineCaseOptionsOverrideMatchCaseAndAreCultureIndependent()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            _session.Refresh("I i", "document", "(?i)i",
                new TextSearchOptions { UseRegex = true, MatchCase = true }, 0);
            Assert.AreEqual(2, _session.Matches.Count);
            _session.Refresh("I i", "document", "(?-i)i",
                new TextSearchOptions { UseRegex = true }, 0);
            Assert.AreEqual(new TextSearchMatch(2, 1), _session.CurrentMatch);
            Assert.AreEqual(1, _session.Matches.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("")]
    public void InvalidRegex_ClearsResultsAndRecoversWhenDisabled(string text)
    {
        Refresh("text", "text", 0);
        _session.Refresh(text, "document", "[", new TextSearchOptions { UseRegex = true }, 0);
        Assert.AreEqual("Invalid regular expression", _session.ValidationMessage);
        Assert.AreEqual(0, _session.Matches.Count);
        Assert.IsNull(_session.Next());
        Assert.IsNull(_session.Previous());
        _session.Refresh("[", "document", "[", default, 0);
        Assert.IsNull(_session.ValidationMessage);
        Assert.AreEqual(1, _session.Matches.Count);
        _session.Refresh(text, "document", "[", new TextSearchOptions { UseRegex = true }, 0);
        _session.Reset();
        Assert.IsNull(_session.ValidationMessage);
    }

    [TestMethod]
    public void Regex_TimeoutDiscardsPartialResults()
    {
        _session.Refresh("ok " + new string('a', 100000) + "!", "document", @"ok|(?:a+)+$",
            new TextSearchOptions { UseRegex = true }, 0);
        Assert.AreEqual("Regex search timed out", _session.ValidationMessage);
        Assert.AreEqual(0, _session.Matches.Count);
        Assert.IsNull(_session.CurrentMatch);
    }

    [TestMethod]
    [DataRow("^|$", 0)]
    [DataRow("(?=a)", 0)]
    [DataRow("a*", 1)]
    [DataRow("(?=a)|b", 1)]
    [DataRow("(?:a)", 1)]
    [DataRow("", 0)]
    public void Regex_ExcludesEmptyMatchesAndAllowsPatternsLongerThanSource(string query, int count)
    {
        _session.Refresh("ab", "document", query, new TextSearchOptions { UseRegex = true }, 0);
        Assert.AreEqual(count, _session.Matches.Count);
        Assert.IsTrue(_session.Matches.All(match => match.Length > 0));
        Assert.IsNull(_session.ValidationMessage);
    }

    [TestMethod]
    public void RegexWholeWord_FiltersReturnedSpansWithoutReinterpretingAlternation()
    {
        var options = new TextSearchOptions { UseRegex = true, MatchWholeWord = true };
        _session.Refresh("cats cat", "document", "cat|cats", options, 0);
        CollectionAssert.AreEqual(new[] { new TextSearchMatch(5, 3) }, _session.Matches.ToArray());
        _session.Refresh("cats cat", "document", "cats|cat", options, 0);
        CollectionAssert.AreEqual(new[] { new TextSearchMatch(0, 4), new TextSearchMatch(5, 3) },
            _session.Matches.ToArray());
        _session.Refresh("C++ C++x", "document", @"C\+\+", options, 0);
        CollectionAssert.AreEqual(new[] { new TextSearchMatch(0, 3) }, _session.Matches.ToArray());
    }

    [TestMethod]
    public void LiteralWholeWord_RejectedSubstringDoesNotHideLaterWholeMatch()
    {
        _session.Refresh("xa-a-a", "document", "a-a",
            new TextSearchOptions { MatchWholeWord = true }, 0);
        CollectionAssert.AreEqual(new[] { new TextSearchMatch(3, 3) }, _session.Matches.ToArray());
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
