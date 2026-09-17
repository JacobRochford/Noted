using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class TextBoxSearchPresenterTests
{
    [TestMethod]
    public void RenderingAllMatches_DoesNotMutateTextOrUndoState()
    {
        RunOnSta(() =>
        {
            using var surface = new TextBoxSurface("foo middle foo", 260, 100);
            var originalText = surface.Editor.Text;
            var originalCanUndo = surface.Editor.CanUndo;
            var session = CreateSession(surface.Editor, "foo", anchor: 0);

            surface.Presenter.Present(session, bringCurrentIntoView: false);
            surface.Render();

            Assert.AreEqual(originalText, surface.Editor.Text);
            Assert.AreEqual(originalCanUndo, surface.Editor.CanUndo);
            Assert.AreEqual(2, surface.Presenter.GetGeometrySnapshot().Matches.Count);
        });
    }

    [TestMethod]
    public void CurrentMatchAndCurrentVisualRow_AreDistinctGeometryStates()
    {
        RunOnSta(() =>
        {
            using var surface = new TextBoxSurface("foo middle foo", 260, 100);
            var session = CreateSession(surface.Editor, "foo", anchor: 8);

            surface.Presenter.Present(session, bringCurrentIntoView: false);
            var geometry = surface.Presenter.GetGeometrySnapshot();

            Assert.AreEqual(2, geometry.Matches.Count);
            Assert.AreEqual(1, geometry.Matches.Count(match => match.IsCurrent));
            Assert.IsNotNull(geometry.CurrentVisualRow);
            Assert.AreEqual(geometry.Viewport.Width, geometry.CurrentVisualRow.Value.Width, 0.01);
            Assert.IsTrue(
                geometry.Matches.Single(match => match.IsCurrent).Segments[0].Width <
                geometry.CurrentVisualRow.Value.Width);
        });
    }

    [TestMethod]
    public void WrappedMatch_IsSplitIntoVisibleVisualRowSegments()
    {
        RunOnSta(() =>
        {
            var query = new string('x', 48);
            using var surface = new TextBoxSurface(query, 115, 150, TextWrapping.Wrap);
            var session = CreateSession(surface.Editor, query, anchor: 0);

            surface.Presenter.Present(session, bringCurrentIntoView: false);
            var match = surface.Presenter.GetGeometrySnapshot().Matches.Single();

            Assert.IsTrue(match.Segments.Count >= 2);
        });
    }

    [TestMethod]
    public void ManualScroll_InvalidatesGeometryWithoutChangingSearchState()
    {
        RunOnSta(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"foo line {index}"));
            using var surface = new TextBoxSurface(text, 220, 100);
            var session = CreateSession(surface.Editor, "foo", anchor: 0);
            surface.Presenter.Present(session, bringCurrentIntoView: false);
            var version = surface.Presenter.InvalidationVersion;

            surface.Editor.ScrollToLine(50);
            surface.Pump();

            Assert.IsTrue(surface.Presenter.InvalidationVersion > version);
            Assert.AreEqual(80, session.Matches.Count);
            Assert.AreEqual(text, surface.Editor.Text);
        });
    }

    [TestMethod]
    public void Resize_InvalidatesGeometry()
    {
        RunOnSta(() =>
        {
            using var surface = new TextBoxSurface(new string('x', 60), 120, 100);
            var session = CreateSession(surface.Editor, "xxx", anchor: 0);
            surface.Presenter.Present(session, bringCurrentIntoView: false);
            var version = surface.Presenter.InvalidationVersion;

            surface.Resize(220, 120);

            Assert.IsTrue(surface.Presenter.InvalidationVersion > version);
        });
    }

    [TestMethod]
    public void WordWrapChange_InvalidatesGeometry()
    {
        RunOnSta(() =>
        {
            using var surface = new TextBoxSurface(new string('x', 60), 120, 100);
            var session = CreateSession(surface.Editor, "xxx", anchor: 0);
            surface.Presenter.Present(session, bringCurrentIntoView: false);
            var version = surface.Presenter.InvalidationVersion;

            surface.Editor.TextWrapping = TextWrapping.NoWrap;

            Assert.IsTrue(surface.Presenter.InvalidationVersion > version);
        });
    }

    [TestMethod]
    public void RenderInvalidation_IsDeferredAndPreservesPresentationState()
    {
        RunOnSta(() =>
        {
            using var surface = new TextBoxSurface("before foo after", 240, 100);
            surface.Editor.Select(2, 0);
            var session = CreateSession(surface.Editor, "foo", anchor: 0);
            surface.Presenter.Present(session, bringCurrentIntoView: false);
            var scrollViewer = (ScrollViewer)surface.Editor.Template.FindName(
                "PART_ContentHost",
                surface.Editor);
            var version = surface.Presenter.InvalidationVersion;
            var verticalOffset = scrollViewer.VerticalOffset;
            var horizontalOffset = scrollViewer.HorizontalOffset;

            surface.Presenter.RequestRenderInvalidation();

            Assert.AreEqual(version, surface.Presenter.InvalidationVersion);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

            Assert.AreEqual(version + 1, surface.Presenter.InvalidationVersion);
            Assert.AreEqual(2, surface.Editor.SelectionStart);
            Assert.AreEqual(0, surface.Editor.SelectionLength);
            Assert.AreEqual(1, session.Matches.Count);
            Assert.AreEqual(1, surface.Presenter.GetGeometrySnapshot().Matches.Count);
            Assert.AreEqual(verticalOffset, scrollViewer.VerticalOffset, 0.01);
            Assert.AreEqual(horizontalOffset, scrollViewer.HorizontalOffset, 0.01);
        });
    }

    [TestMethod]
    public void HorizontalReveal_WhenWordWrapIsOff_ScrollsToCurrentMatch()
    {
        RunOnSta(() =>
        {
            var text = new string('a', 120) + "target";
            using var surface = new TextBoxSurface(text, 150, 100, TextWrapping.NoWrap);
            var session = CreateSession(surface.Editor, "target", anchor: 120);

            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            var scrollViewer = (ScrollViewer)surface.Editor.Template.FindName(
                "PART_ContentHost",
                surface.Editor);
            Assert.IsTrue(scrollViewer.HorizontalOffset > 0);
        });
    }

    [TestMethod]
    public void VisibleMatch_DoesNotChangeScrollOffsets()
    {
        RunOnSta(() =>
        {
            using var surface = new TextBoxSurface("before foo after", 240, 100, TextWrapping.NoWrap);
            var session = CreateSession(surface.Editor, "foo", anchor: 0);
            var scrollViewer = (ScrollViewer)surface.Editor.Template.FindName(
                "PART_ContentHost",
                surface.Editor);

            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            Assert.AreEqual(0, scrollViewer.VerticalOffset, 0.01);
            Assert.AreEqual(0, scrollViewer.HorizontalOffset, 0.01);
        });
    }

    [TestMethod]
    public void ActiveFind_DoesNotMutateNativeEditorSelection()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("foo xx foo xx foo", 300, 140);
            surface.Editor.Select(3, 2);
            var selectionStart = surface.Editor.SelectionStart;
            var selectionLength = surface.Editor.SelectionLength;
            surface.OpenFind();
            surface.SetQuery("foo");

            void AssertActiveFindState()
            {
                Assert.AreEqual(selectionStart, surface.Editor.SelectionStart);
                Assert.AreEqual(selectionLength, surface.Editor.SelectionLength);
                var overlayCurrent = surface.Presenter.GetGeometrySnapshot().Matches.Single(match => match.IsCurrent);
                Assert.AreEqual(surface.Session.CurrentMatch, overlayCurrent.Match);
            }

            var initialMatch = surface.Session.CurrentMatch;
            AssertActiveFindState();

            surface.MoveFind(forward: true);
            var secondMatch = surface.Session.CurrentMatch;
            AssertActiveFindState();
            surface.MoveFind(forward: true);
            AssertActiveFindState();

            Assert.AreNotEqual(initialMatch, secondMatch);
            Assert.AreNotEqual(secondMatch, surface.Session.CurrentMatch);
        });
    }

    [TestMethod]
    public void RepeatedNavigationWithoutManualMovement_UsesCurrentMatchAndLeavesNativeSelectionAlone()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("foo xx foo xx foo", 300, 140);
            surface.Editor.Select(1, 0);
            surface.OpenFind();
            surface.SetQuery("foo");
            Assert.AreEqual(new TextSearchMatch(0, 3), surface.Session.CurrentMatch);

            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(7, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(14, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(0, 3), surface.Session.CurrentMatch);

            Assert.AreEqual(1, surface.Editor.SelectionStart);
            Assert.AreEqual(0, surface.Editor.SelectionLength);

            surface.MoveFind(forward: false);
            Assert.AreEqual(new TextSearchMatch(14, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: false);
            Assert.AreEqual(new TextSearchMatch(7, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(1, surface.Editor.SelectionStart);
            Assert.AreEqual(0, surface.Editor.SelectionLength);
        });
    }

    [TestMethod]
    public void ManualCaretReanchor_IsConsumedOnceForForwardAndBackwardNavigation()
    {
        RunOnSta(() =>
        {
            const string text = "foo foo foo foo foo foo foo foo foo foo foo foo foo foo foo";
            using var surface = new MiniPadLikeFindSurface(text, 320, 150);
            surface.OpenFind();
            surface.SetQuery("foo");
            for (var index = 0; index < 3; index++)
                surface.MoveFind(forward: true);

            surface.Editor.Select(43, 0);
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(44, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(48, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(43, surface.Editor.SelectionStart);

            surface.Editor.Select(42, 0);
            surface.Editor.Select(43, 0);
            surface.MoveFind(forward: false);
            Assert.AreEqual(new TextSearchMatch(40, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: false);
            Assert.AreEqual(new TextSearchMatch(36, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(43, surface.Editor.SelectionStart);
        });
    }

    [TestMethod]
    public void F3WhileFindIsClosed_StillUsesGenuineManualCaretAsOneShotAnchor()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("foo foo foo", 300, 140);
            surface.OpenFind();
            surface.SetQuery("foo");
            surface.MoveFind(forward: true);
            surface.CloseFind();

            surface.Editor.Select(1, 0);
            surface.MoveFind(forward: true);

            Assert.AreEqual(new TextSearchMatch(0, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(1, surface.Editor.SelectionStart);
        });
    }

    [TestMethod]
    public void KnownBug_RedundantSameRangeSelectionNotificationCanArmManualNavigationAnchor()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("foo xx foo xx foo", 300, 140);
            surface.Editor.Select(1, 0);
            surface.OpenFind();
            surface.SetQuery("foo");
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(7, 3), surface.Session.CurrentMatch);

            surface.Editor.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.TextBoxBase.SelectionChangedEvent));
            surface.MoveFind(forward: true);

            Assert.AreEqual(new TextSearchMatch(0, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(1, surface.Editor.SelectionStart);
            Assert.AreEqual(0, surface.Editor.SelectionLength);
        });
    }

    [TestMethod]
    public void ManualRangeReanchor_UsesDirectionalEndpointOnceThenCurrentMatch()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("foo xx foo xx foo xx foo", 300, 140);
            surface.OpenFind();
            surface.SetQuery("foo");

            surface.Editor.Select(8, 4);
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(14, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: true);
            Assert.AreEqual(new TextSearchMatch(21, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(8, surface.Editor.SelectionStart);
            Assert.AreEqual(4, surface.Editor.SelectionLength);

            surface.Editor.Select(9, 0);
            surface.Editor.Select(8, 4);
            surface.MoveFind(forward: false);
            Assert.AreEqual(new TextSearchMatch(7, 3), surface.Session.CurrentMatch);
            surface.MoveFind(forward: false);
            Assert.AreEqual(new TextSearchMatch(0, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(8, surface.Editor.SelectionStart);
            Assert.AreEqual(4, surface.Editor.SelectionLength);
        });
    }

    [TestMethod]
    public void MiniPadQueryReplacement_RGISToHostToOnUsesGenuineUserAnchor()
    {
        RunOnSta(() =>
        {
            const string text = "RGIS online ... host GIS data on later";
            using var surface = new MiniPadLikeFindSurface(text, 320, 150);
            surface.Editor.Select(0, 0);
            surface.OpenFind();

            surface.SetQuery("RGIS");
            Assert.AreEqual(text.IndexOf("RGIS", StringComparison.Ordinal), surface.Session.CurrentMatch?.Start);
            surface.SetQuery("host");
            Assert.AreEqual(text.IndexOf("host", StringComparison.Ordinal), surface.Session.CurrentMatch?.Start);
            surface.SetQuery("on");

            Assert.AreEqual(text.IndexOf("on", StringComparison.Ordinal), surface.Session.CurrentMatch?.Start);
            Assert.AreEqual(0, surface.Session.CurrentMatchIndex);
            Assert.AreEqual(0, surface.Editor.SelectionStart);
            Assert.AreEqual(0, surface.Editor.SelectionLength);
        });
    }

    [TestMethod]
    public void QueryReplacement_AfterMultipleNavigationOperationsDoesNotUseLastSearchOccurrenceAsAnchor()
    {
        RunOnSta(() =>
        {
            const string text = "beta alpha alpha alpha beta";
            using var surface = new MiniPadLikeFindSurface(text, 320, 150);
            surface.Editor.Select(0, 0);
            surface.OpenFind();
            surface.SetQuery("alpha");
            surface.MoveFind(forward: true);
            surface.MoveFind(forward: true);
            Assert.AreEqual(text.LastIndexOf("alpha", StringComparison.Ordinal), surface.Session.CurrentMatch?.Start);
            Assert.AreEqual(0, surface.Editor.SelectionStart);

            surface.SetQuery("beta");

            Assert.AreEqual(0, surface.Session.CurrentMatch?.Start);
            Assert.AreEqual(0, surface.Session.CurrentMatchIndex);
            Assert.AreEqual(0, surface.Editor.SelectionStart);
        });
    }

    [TestMethod]
    public void IncrementalQueryShortening_PreservesCurrentOccurrenceAndNativeEditorSelection()
    {
        RunOnSta(() =>
        {
            const string text = "den early dental later den";
            var dentalStart = text.IndexOf("dental", StringComparison.Ordinal);
            using var surface = new MiniPadLikeFindSurface(text, 320, 150);
            surface.Editor.Select(dentalStart - 1, 0);
            surface.OpenFind();
            surface.SetQuery("dental");
            Assert.AreEqual(new TextSearchMatch(dentalStart, 6), surface.Session.CurrentMatch);

            foreach (var query in new[] { "denta", "dent", "den" })
            {
                surface.SetQuery(query);
                Assert.AreEqual(new TextSearchMatch(dentalStart, query.Length), surface.Session.CurrentMatch);
                Assert.AreEqual(dentalStart - 1, surface.Editor.SelectionStart);
                Assert.AreEqual(0, surface.Editor.SelectionLength);
            }
        });
    }

    [TestMethod]
    public void CtrlFSelectedTextSeeding_PreservesGenuineEditorSelectionDuringFind()
    {
        RunOnSta(() =>
        {
            const string text = "before foo after foo";
            using var surface = new MiniPadLikeFindSurface(text, 320, 150);
            var selectedStart = text.IndexOf("foo", StringComparison.Ordinal);
            surface.Editor.Select(selectedStart, 3);

            surface.OpenFind();

            Assert.AreEqual("foo", surface.FindTextBox.Text);
            Assert.AreEqual(selectedStart, surface.Editor.SelectionStart);
            Assert.AreEqual(3, surface.Editor.SelectionLength);
            Assert.AreEqual(new TextSearchMatch(selectedStart, 3), surface.Session.CurrentMatch);
            var overlayCurrent = surface.Presenter.GetGeometrySnapshot().Matches.Single(match => match.IsCurrent);
            Assert.AreEqual(surface.Session.CurrentMatch, overlayCurrent.Match);
        });
    }

    [TestMethod]
    public void EmptyOrMissingQuery_PreservesGenuineEditorSelection()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("before foo after", 320, 150);
            surface.Editor.Select(7, 3);
            surface.OpenFind();
            surface.SetQuery("foo");

            surface.SetQuery(string.Empty);
            Assert.AreEqual(7, surface.Editor.SelectionStart);
            Assert.AreEqual(3, surface.Editor.SelectionLength);
            Assert.AreEqual(0, surface.Presenter.GetGeometrySnapshot().Matches.Count);

            surface.SetQuery("missing");
            Assert.AreEqual(7, surface.Editor.SelectionStart);
            Assert.AreEqual(3, surface.Editor.SelectionLength);
            Assert.IsNull(surface.Session.CurrentMatch);
            Assert.AreEqual(0, surface.Presenter.GetGeometrySnapshot().Matches.Count);
        });
    }

    [TestMethod]
    public void CloseFind_ExplicitlyHandsCurrentMatchBackToNativeEditorSelection()
    {
        RunOnSta(() =>
        {
            const string text = "foo xx foo xx foo";
            using var surface = new MiniPadLikeFindSurface(text, 300, 140);
            surface.Editor.Select(1, 0);
            surface.OpenFind();
            surface.SetQuery("foo");
            surface.MoveFind(forward: true);
            surface.MoveFind(forward: true);
            var finalMatch = surface.Session.CurrentMatch!.Value;
            Assert.AreEqual(1, surface.Editor.SelectionStart);
            Assert.AreEqual(0, surface.Editor.SelectionLength);

            surface.CloseFind();

            Assert.AreEqual(finalMatch.Start, surface.Editor.SelectionStart);
            Assert.AreEqual(finalMatch.Length, surface.Editor.SelectionLength);
            Assert.AreEqual(finalMatch.Length, surface.Editor.SelectedText.Length);
            Assert.AreEqual(0, surface.Presenter.GetGeometrySnapshot().Matches.Count);
            Assert.IsNull(surface.Presenter.GetGeometrySnapshot().CurrentVisualRow);
            Assert.IsNull(surface.Session.CurrentMatch);
            Assert.AreEqual(0, surface.Session.Matches.Count);
        });
    }

    [TestMethod]
    public void CloseFind_WithNoCurrentMatchPreservesGenuineEditorSelection()
    {
        RunOnSta(() =>
        {
            using var surface = new MiniPadLikeFindSurface("before foo after", 300, 140);
            surface.Editor.Select(7, 3);
            surface.OpenFind();
            surface.SetQuery("missing");

            surface.CloseFind();

            Assert.AreEqual(7, surface.Editor.SelectionStart);
            Assert.AreEqual(3, surface.Editor.SelectionLength);
            Assert.AreEqual("foo", surface.Editor.SelectedText);
        });
    }

    [TestMethod]
    public void OffscreenIncrementalDentalSearch_UsesOverlayWithoutMutatingNativeSelection()
    {
        RunOnSta(() =>
        {
            var prefix = string.Concat(Enumerable.Repeat("aaa\n", 40));
            var text = prefix + "dental target";
            var dentalStart = prefix.Length;
            using var surface = new MiniPadLikeFindSurface(text, 220, 105);
            surface.Editor.Select(0, 0);
            surface.OpenFind();
            var initialOffset = surface.Editor.VerticalOffset;

            foreach (var query in new[] { "d", "de", "den", "dent", "denta", "dental" })
            {
                surface.SetQuery(query);
                surface.Pump();

                Assert.AreEqual(new TextSearchMatch(dentalStart, query.Length), surface.Session.CurrentMatch);
                Assert.AreEqual(0, surface.Editor.SelectionStart);
                Assert.AreEqual(0, surface.Editor.SelectionLength);
                var overlayCurrent = surface.Presenter.GetGeometrySnapshot().Matches.Single(match => match.IsCurrent);
                Assert.AreEqual(surface.Session.CurrentMatch, overlayCurrent.Match);
                Assert.IsTrue(overlayCurrent.Segments.Count > 0,
                    $"The current {query} match should have visible overlay geometry after reveal.");
                var leading = surface.Editor.GetRectFromCharacterIndex(dentalStart, false);
                var trailing = surface.Editor.GetRectFromCharacterIndex(dentalStart + query.Length - 1, true);
                Assert.IsTrue(overlayCurrent.Segments.Any(segment =>
                        segment.Left <= leading.Left + 0.5 &&
                        segment.Right >= trailing.Right - 0.5),
                    $"The overlay should cover the complete visible {query} match.");
                AssertCurrentMatchFullyVisible(surface.Editor, surface.Session);
            }

            Assert.IsTrue(surface.Editor.VerticalOffset > initialOffset + 0.01);
        });
    }

    [TestMethod]
    public void VerticalReveal_ResultOneVisualLineBelowViewport_BringsCurrentResultIntoView()
    {
        RunOnSta(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"target line {index}"));
            using var surface = new TextBoxSurface(text, 240, 120);
            var targetLine = surface.Editor.GetLastVisibleLineIndex() + 1;
            Assert.IsTrue(targetLine > 0 && targetLine < surface.Editor.LineCount);
            var targetStart = surface.Editor.GetCharacterIndexFromLineIndex(targetLine);
            var session = CreateSession(surface.Editor, "target", targetStart);
            var beforeOffset = surface.Editor.VerticalOffset;

            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            AssertCurrentMatchFullyVisible(surface.Editor, session);
            Assert.IsTrue(surface.Editor.VerticalOffset > beforeOffset + 0.01);
        });
    }

    [TestMethod]
    public void RepeatedFindNextAcrossViewport_KeepsEveryCurrentResultVisible()
    {
        RunOnSta(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 80).Select(index => $"target line {index}"));
            using var surface = new TextBoxSurface(text, 240, 105);
            var session = CreateSession(surface.Editor, "target", anchor: 0);
            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            for (var index = 0; index < 20; index++)
            {
                AssertCurrentMatchFullyVisible(surface.Editor, session);
                session.Next();
                surface.Presenter.Present(session, bringCurrentIntoView: true);
                surface.Pump();
            }

            AssertCurrentMatchFullyVisible(surface.Editor, session);
        });
    }

    [TestMethod]
    public void VerticalReveal_FarJumpPlacesCurrentResultNearUpperThird()
    {
        RunOnSta(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"target line {index}"));
            using var surface = new TextBoxSurface(text, 240, 140);
            const int targetLine = 60;
            var targetStart = surface.Editor.GetCharacterIndexFromLineIndex(targetLine);
            var session = CreateSession(surface.Editor, "target", targetStart);

            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            AssertCurrentMatchFullyVisible(surface.Editor, session);
            var viewport = surface.Presenter.GetGeometrySnapshot().Viewport;
            var currentRect = surface.Editor.GetRectFromCharacterIndex(session.CurrentMatch!.Value.Start, false);
            var expectedTop = viewport.Top + (viewport.Height / 3);
            Assert.AreEqual(expectedTop, currentRect.Top, Math.Max(currentRect.Height, 1.0));
        });
    }

    [TestMethod]
    public void RequiredVerticalReveal_ChangesActualVerticalScrollOffset()
    {
        RunOnSta(() =>
        {
            var text = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"target line {index}"));
            using var surface = new TextBoxSurface(text, 240, 120);
            var targetStart = surface.Editor.GetCharacterIndexFromLineIndex(50);
            var session = CreateSession(surface.Editor, "target", targetStart);
            var beforeOffset = surface.Editor.VerticalOffset;

            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            Assert.IsTrue(surface.Editor.VerticalOffset > beforeOffset + 0.01);
            AssertCurrentMatchFullyVisible(surface.Editor, session);
        });
    }

    [TestMethod]
    public void VerticalReveal_WrappedCurrentResultBringsAllVisualRowsIntoView()
    {
        RunOnSta(() =>
        {
            var query = new string('x', 48);
            var prefix = string.Join("\n", Enumerable.Range(0, 30).Select(index => $"line {index}")) + "\n";
            using var surface = new TextBoxSurface(prefix + query, 115, 150, TextWrapping.Wrap);
            var session = CreateSession(surface.Editor, query, prefix.Length);
            var match = session.CurrentMatch!.Value;
            var firstMatchLine = surface.Editor.GetLineIndexFromCharacterIndex(match.Start);
            var lastMatchLine = surface.Editor.GetLineIndexFromCharacterIndex(match.End - 1);
            Assert.IsTrue(lastMatchLine > firstMatchLine, "The regression setup must wrap the current result.");

            surface.Presenter.Present(session, bringCurrentIntoView: true);
            surface.Pump();

            AssertCurrentMatchFullyVisible(surface.Editor, session);
        });
    }

    private static void AssertCurrentMatchFullyVisible(TextBox editor, TextSearchSession session)
    {
        Assert.IsTrue(session.CurrentMatch.HasValue);
        var match = session.CurrentMatch.Value;
        var firstMatchLine = editor.GetLineIndexFromCharacterIndex(match.Start);
        var lastMatchLine = editor.GetLineIndexFromCharacterIndex(match.End - 1);
        var firstVisibleLine = editor.GetFirstVisibleLineIndex();
        var lastVisibleLine = editor.GetLastVisibleLineIndex();

        Assert.IsTrue(
            firstMatchLine >= firstVisibleLine && lastMatchLine <= lastVisibleLine,
            $"Current match lines {firstMatchLine}-{lastMatchLine} were outside visible lines {firstVisibleLine}-{lastVisibleLine}.");
    }

    private static TextSearchSession CreateSession(TextBox editor, string query, int anchor)
    {
        var session = new TextSearchSession();
        session.Refresh(editor.Text, editor, query, default, anchor);
        return session;
    }
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "STA search presentation test did not complete.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TextBoxSurface : IDisposable
    {
        private readonly HwndSource _source;
        private readonly AdornerDecorator _decorator;

        internal TextBoxSurface(
            string text,
            double width,
            double height,
            TextWrapping wrapping = TextWrapping.Wrap,
            bool presenterBeforeLoad = false)
        {
            _source = new HwndSource(new HwndSourceParameters("Noted search presentation test")
            {
                Width = (int)width,
                Height = (int)height,
                WindowStyle = unchecked((int)0x80000000)
            });
            Editor = new TextBox
            {
                Text = text,
                AcceptsReturn = true,
                TextWrapping = wrapping,
                HorizontalScrollBarVisibility = wrapping == TextWrapping.Wrap
                    ? ScrollBarVisibility.Disabled
                    : ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Padding = new Thickness(8),
                BorderThickness = new Thickness(0)
            };
            _decorator = new AdornerDecorator
            {
                Width = width,
                Height = height,
                Child = Editor
            };
            _decorator.Resources["NotedSearchMatchBrush"] = new SolidColorBrush(Color.FromArgb(70, 50, 120, 180));
            _decorator.Resources["NotedSearchCurrentBrush"] = new SolidColorBrush(Color.FromArgb(110, 50, 120, 180));
            _decorator.Resources["NotedSearchCurrentBorderBrush"] = Brushes.Navy;
            _decorator.Resources["NotedSearchCurrentLineBrush"] = new SolidColorBrush(Color.FromArgb(25, 50, 120, 180));
            var presenter = presenterBeforeLoad ? new TextBoxSearchPresenter(Editor) : null;
            _source.RootVisual = _decorator;
            Pump();
            Presenter = presenter ?? new TextBoxSearchPresenter(Editor);
            Pump();
        }

        internal TextBox Editor { get; }

        internal TextBoxSearchPresenter Presenter { get; }
        internal void Resize(double width, double height)
        {
            _decorator.Width = width;
            _decorator.Height = height;
            Pump();
        }

        internal void Pump()
        {
            _decorator.Measure(new Size(_decorator.Width, _decorator.Height));
            _decorator.Arrange(new Rect(0, 0, _decorator.Width, _decorator.Height));
            _decorator.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        internal byte[] Render()
        {
            Pump();
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(_decorator.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(_decorator.ActualHeight)),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(_decorator);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        public void Dispose()
        {
            Presenter.Dispose();
            _source.Dispose();
        }
    }

    private sealed class MiniPadLikeFindSurface : IDisposable
    {
        private readonly HwndSource _source;
        private readonly DockPanel _root;
        private readonly Border _findPanel;

        internal MiniPadLikeFindSurface(string text, double width, double height)
        {
            _source = new HwndSource(new HwndSourceParameters("Noted MiniPad first-use search test")
            {
                Width = (int)width,
                Height = (int)height,
                WindowStyle = unchecked((int)0x80000000)
            });
            Editor = new TextBox
            {
                Text = text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Padding = new Thickness(8),
                BorderThickness = new Thickness(0)
            };
            FindTextBox = new TextBox();
            _findPanel = new Border
            {
                Height = 48,
                Visibility = Visibility.Collapsed,
                Child = FindTextBox
            };
            DockPanel.SetDock(_findPanel, Dock.Top);
            var decorator = new AdornerDecorator { Child = Editor };
            _root = new DockPanel
            {
                Width = width,
                Height = height
            };
            _root.Resources["NotedSearchMatchBrush"] = Brushes.Magenta;
            _root.Resources["NotedSearchCurrentBrush"] = Brushes.Magenta;
            _root.Resources["NotedSearchCurrentBorderBrush"] = Brushes.Magenta;
            _root.Resources["NotedSearchCurrentLineBrush"] = Brushes.Magenta;
            _root.Children.Add(_findPanel);
            _root.Children.Add(decorator);

            Session = new TextSearchSession();
            Presenter = new TextBoxSearchPresenter(Editor);
            FindTextBox.TextChanged += FindTextBox_TextChanged;
            _source.RootVisual = _root;
            Pump();
        }

        internal TextBox Editor { get; }

        internal TextBox FindTextBox { get; }

        internal TextSearchSession Session { get; }

        internal TextBoxSearchPresenter Presenter { get; }

        internal void OpenFind()
        {
            var openingFind = _findPanel.Visibility != Visibility.Visible;
            _findPanel.Visibility = Visibility.Visible;
            if (openingFind)
                Presenter.BeginFind();
            if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n'))
                FindTextBox.Text = Editor.SelectedText;
            RefreshFind();
            Pump();
            Assert.IsTrue(FindTextBox.Focus(), "The Find textbox must acquire keyboard focus.");
            Assert.IsTrue(FindTextBox.IsKeyboardFocusWithin);
            FindTextBox.SelectAll();
        }

        internal void Type(string text) => FindTextBox.AppendText(text);

        internal void SetQuery(string query) => FindTextBox.Text = query;

        internal void CloseFind()
        {
            _findPanel.Visibility = Visibility.Collapsed;
            Presenter.EndFind(Session.CurrentMatch);
            Session.Reset();
            Editor.Focus();
            Pump();
        }

        internal void MoveFind(bool forward)
        {
            Session.Refresh(
                Editor.Text,
                Editor,
                FindTextBox.Text,
                default,
                Presenter.GetQueryRefreshAnchor());
            if (Session.Matches.Count == 0)
                return;

            var manualAnchor = Presenter.ConsumePendingManualNavigationAnchor(forward);
            if (forward)
                Session.Next(manualAnchor);
            else
                Session.Previous(manualAnchor);
            Presenter.Present(Session, bringCurrentIntoView: true);
        }

        internal void Pump()
        {
            _root.Measure(new Size(_root.Width, _root.Height));
            _root.Arrange(new Rect(0, 0, _root.Width, _root.Height));
            _root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        internal byte[] Render()
        {
            Pump();
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(_root.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(_root.ActualHeight)),
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(_root);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }

        public void Dispose()
        {
            FindTextBox.TextChanged -= FindTextBox_TextChanged;
            Presenter.Dispose();
            _source.Dispose();
        }

        private void FindTextBox_TextChanged(object sender, TextChangedEventArgs args)
        {
            RefreshFind();
        }

        private void RefreshFind()
        {
            Session.Refresh(
                Editor.Text,
                Editor,
                FindTextBox.Text,
                default,
                Presenter.GetQueryRefreshAnchor());
            Presenter.Present(Session, bringCurrentIntoView: true);
        }
    }
}
