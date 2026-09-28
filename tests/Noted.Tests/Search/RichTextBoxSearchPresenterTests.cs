using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Search;

namespace Noted.Tests.Search;

[TestClass]
public sealed class RichTextBoxSearchPresenterTests
{
    [TestMethod]
    public void Snapshot_MapsEveryTextBoundaryAcrossFormattingAndUtf16Characters()
    {
        RunOnSta(() =>
        {
            var first = new Run("foo ");
            var second = new Run("bar ") { FontWeight = FontWeights.Bold };
            var third = new Run("\U0001F600 end");
            var document = new FlowDocument(new Paragraph());
            var paragraph = (Paragraph)document.Blocks.FirstBlock!;
            paragraph.Inlines.Add(first);
            paragraph.Inlines.Add(second);
            paragraph.Inlines.Add(third);
            var snapshot = new RichTextSearchSnapshot(document);
            Assert.AreEqual("foo bar \U0001F600 end", snapshot.Text);
            var offset = 0;
            foreach (var run in new[] { first, second, third })
            {
                for (var index = 0; index <= run.Text.Length; index++)
                    Assert.AreEqual(offset + index, snapshot.GetOffset(run.ContentStart.GetPositionAtOffset(index)!));
                offset += run.Text.Length;
            }
            Assert.AreEqual("o bar", snapshot.CreateRange(new(2, 5)).Text);
            Assert.AreEqual("\U0001F600", snapshot.CreateRange(new(8, 2)).Text);
        });
    }

    [TestMethod]
    public void Snapshot_MapsParagraphAndLineBreakBoundaries()
    {
        RunOnSta(() =>
        {
            var first = new Run("foo");
            var last = new Run("bar");
            var document = new FlowDocument(new Paragraph(first));
            document.Blocks.Add(new Paragraph(last));
            var snapshot = new RichTextSearchSnapshot(document);
            Assert.AreEqual("foo\r\nbar", snapshot.Text);
            Assert.AreEqual(3, snapshot.GetOffset(first.ContentEnd));
            Assert.AreEqual(5, snapshot.GetOffset(last.ContentStart));
            Assert.AreEqual(8, snapshot.GetOffset(last.ContentEnd));
            Assert.AreEqual("foo\r\nbar", snapshot.CreateRange(new(0, 8)).Text);

            var paragraph = new Paragraph(new Run("foo"));
            paragraph.Inlines.Add(new LineBreak());
            var afterBreak = new Run("bar");
            paragraph.Inlines.Add(afterBreak);
            var withBreak = new RichTextSearchSnapshot(new FlowDocument(paragraph));
            Assert.AreEqual(withBreak.Text.IndexOf("bar", StringComparison.Ordinal), withBreak.GetOffset(afterBreak.ContentStart));
        });
    }

    [TestMethod]
    public void CaretAtMatchEnd_IsNotInsidePrecedingMatch()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo");
            surface.Select(3, 0);
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            Assert.AreEqual(1, surface.Session.CurrentMatchIndex);
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OpeningAndQueryRefresh_UseSelectionStartRegardlessOfDirection(bool forward)
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo xx foo");
            SelectRangeWithKeyboard(surface.Editor, 1, 8, forward);
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            Assert.AreEqual(0, surface.Session.CurrentMatchIndex);
            surface.Session.Next();
            surface.Presenter.Present(surface.Session, false);
            surface.Refresh("fo");
            Assert.AreEqual(0, surface.Session.CurrentMatchIndex);
            Assert.AreEqual("oo xx f", surface.Editor.Selection.Text);
        });
    }

    [TestMethod]
    public void SameRangeWithReversedDirection_DoesNotChangeFindState()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo xx foo");
            SelectRangeWithKeyboard(surface.Editor, 1, 8, forward: true);
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Session.Next();
            surface.Presenter.Present(surface.Session, false);
            surface.Editor.BeginChange();
            try { SelectRangeWithKeyboard(surface.Editor, 1, 8, forward: false); }
            finally { surface.Editor.EndChange(); }
            Assert.AreEqual(1, surface.Session.CurrentMatchIndex);
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
            surface.Refresh("fo");
            Assert.AreEqual(0, surface.Session.CurrentMatchIndex);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ManualSelection_ArmsSelectionStartRegardlessOfDirection(bool forward)
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo xx foo");
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            SelectRangeWithKeyboard(surface.Editor, 1, 8, forward);
            Assert.AreEqual(1, surface.Presenter.ConsumePendingManualNavigationAnchor());
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
        });
    }

    private static void SelectRangeWithKeyboard(RichTextBox editor, int start, int end, bool forward)
    {
        editor.Focus();
        var snapshot = new RichTextSearchSnapshot(editor.Document);
        var pointer = snapshot.GetStart(forward ? start : end);
        editor.Selection.Select(pointer, pointer);
        var command = forward ? EditingCommands.SelectRightByCharacter : EditingCommands.SelectLeftByCharacter;
        for (var index = start; index < end; index++)
            command.Execute(null, editor);
        Assert.AreEqual(start, snapshot.GetOffset(editor.Selection.Start));
        Assert.AreEqual(end, snapshot.GetOffset(editor.Selection.End));
    }

    [TestMethod]
    public void RedundantSelectionEvents_DoNotChangeNavigationOrActiveHighlight()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo xx foo");
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Session.Next();
            surface.Presenter.Present(surface.Session, false);
            surface.Editor.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.TextBoxBase.SelectionChangedEvent));
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
            Assert.AreEqual(1, surface.Session.CurrentMatchIndex);
            surface.Select(1, 0);
            Assert.AreEqual(1, surface.Session.CurrentMatchIndex);
            Assert.AreEqual(1, surface.Presenter.ConsumePendingManualNavigationAnchor());
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
            Assert.AreEqual(new TextSearchMatch(7, 3), surface.Presenter.GetGeometrySnapshot().Matches.Single(match => match.IsCurrent).Match);
        });
    }

    [TestMethod]
    public void Presentation_HighlightsAllMatchesWithoutChangingSelectionFormattingOrUndo()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo xx foo");
            surface.Select(3, 3);
            var selected = surface.Editor.Selection.Text;
            var canUndo = surface.Editor.CanUndo;
            var background = surface.Editor.Selection.GetPropertyValue(TextElement.BackgroundProperty);
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Session.Next();
            surface.Presenter.Present(surface.Session, true);
            surface.Pump();
            Assert.AreEqual(selected, surface.Editor.Selection.Text);
            Assert.AreEqual(canUndo, surface.Editor.CanUndo);
            Assert.AreEqual(background, surface.Editor.Selection.GetPropertyValue(TextElement.BackgroundProperty));
            var geometry = surface.Presenter.GetGeometrySnapshot();
            Assert.AreEqual(3, geometry.Matches.Count);
            Assert.AreEqual(1, geometry.Matches.Count(match => match.IsCurrent));
            Assert.IsTrue(geometry.Matches.All(match => match.Segments.Count > 0));
        });
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("missing")]
    public void EmptyOrMissingQuery_ClearsVisualsWithoutChangingSelection(string query)
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo");
            surface.Select(1, 2);
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Refresh(query);
            surface.Pump();
            Assert.AreEqual(0, surface.Presenter.GetGeometrySnapshot().Matches.Count);
            Assert.IsNull(surface.Session.CurrentMatch);
            Assert.AreEqual("oo", surface.Editor.Selection.Text);
        });
    }

    [TestMethod]
    public void Close_RemovesVisualsAndPreservesSelection()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo");
            surface.Select(3, 3);
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Presenter.EndFind();
            surface.Pump();
            Assert.AreEqual(" xx", surface.Editor.Selection.Text);
            Assert.AreEqual(0, surface.Presenter.GetGeometrySnapshot().Matches.Count);
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
        });
    }

    [TestMethod]
    public void ReplacementAcrossRuns_UsesMappedRangeAndDoesNotArmManualNavigation()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("");
            surface.Editor.Document = new FlowDocument(new Paragraph());
            var paragraph = (Paragraph)surface.Editor.Document.Blocks.FirstBlock!;
            paragraph.Inlines.Add(new Run("fo"));
            paragraph.Inlines.Add(new Run("o xx foo") { FontWeight = FontWeights.Bold });
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Presenter.Replace([surface.Session.Matches[0]], "bar");
            Assert.AreEqual("bar xx foo", new RichTextSearchSnapshot(surface.Editor.Document).Text);
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
            surface.Editor.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.TextBoxBase.SelectionChangedEvent));
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
            Assert.IsTrue(surface.Editor.CanUndo);
            surface.Editor.Undo();
            Assert.AreEqual("foo xx foo", new RichTextSearchSnapshot(surface.Editor.Document).Text);
            surface.Refresh("foo");
            surface.Presenter.Replace(surface.Session.Matches, "bar");
            Assert.AreEqual("bar xx bar", new RichTextSearchSnapshot(surface.Editor.Document).Text);
            Assert.IsNull(surface.Presenter.ConsumePendingManualNavigationAnchor());
        });
    }

    [TestMethod]
    public void OffscreenResult_IsRevealedWithoutSelectingOrJumpingWhenAlreadyVisible()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface(string.Join("\n", Enumerable.Repeat("ordinary text", 60)) + "\nfoo");
            surface.Presenter.BeginFind();
            var caret = surface.Editor.CaretPosition;
            surface.Refresh("foo");
            surface.Pump();
            Assert.AreEqual(0, surface.Editor.VerticalOffset, "Opening must not reveal the result.");
            surface.Presenter.Present(surface.Session, true);
            surface.Pump();
            Assert.IsTrue(surface.Editor.VerticalOffset > 0);
            Assert.IsTrue(surface.Presenter.GetGeometrySnapshot().Matches.Single().Segments.Count > 0);
            Assert.AreEqual(0, caret.CompareTo(surface.Editor.CaretPosition));
            var offset = surface.Editor.VerticalOffset;
            surface.Presenter.Present(surface.Session, true);
            surface.Pump();
            Assert.AreEqual(offset, surface.Editor.VerticalOffset);
        });
    }

    [TestMethod]
    public void WrappedFormattedResult_HasMultipleVisualSegments()
    {
        RunOnSta(() =>
        {
            const string text = "a long search result with several words that wraps";
            using var surface = new Surface(text, 150);
            surface.Presenter.BeginFind();
            surface.Refresh(text);
            var geometry = surface.Presenter.GetGeometrySnapshot();
            Assert.IsTrue(geometry.Matches.Single().Segments.Select(rect => rect.Top).Distinct().Count() > 1);
            surface.Editor.FontSize = 20;
            surface.Pump();
            Assert.IsTrue(surface.Presenter.GetGeometrySnapshot().Matches.Single().Segments.Count > 1);
        });
    }

    [TestMethod]
    public void HorizontalReveal_UsesMatchBoundsWithoutChangingSelection()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface(new string('x', 140) + " foo");
            surface.Editor.Document.PageWidth = 2000;
            surface.Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            surface.Pump();
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            Assert.AreEqual(0, surface.Editor.HorizontalOffset);
            surface.Presenter.Present(surface.Session, true);
            surface.Pump();
            Assert.IsTrue(surface.Editor.HorizontalOffset > 0);
            Assert.IsTrue(surface.Presenter.GetGeometrySnapshot().Matches.Single().Segments.Count > 0);
            Assert.IsTrue(surface.Editor.Selection.IsEmpty);
        });
    }

    [TestMethod]
    public void DocumentEdits_RefreshHighlightsAndClearRemovedResults()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo");
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            surface.Session.Next();
            surface.Presenter.Present(surface.Session, false);
            surface.Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!.InsertTextInRun("prefix ");
            surface.Refresh("foo");
            Assert.AreEqual(new TextSearchMatch(14, 3), surface.Session.CurrentMatch);
            Assert.AreEqual(2, surface.Presenter.GetGeometrySnapshot().Matches.Count);
            surface.Editor.Document.Blocks.Clear();
            surface.Refresh("foo");
            surface.Pump();
            Assert.AreEqual(0, surface.Presenter.GetGeometrySnapshot().Matches.Count);
            Assert.IsNull(surface.Session.CurrentMatch);
        });
    }

    [TestMethod]
    public void Overlay_RendersWithoutNativeSelectionAndDisappearsOnClose()
    {
        RunOnSta(() =>
        {
            using var surface = new Surface("foo xx foo");
            var original = surface.Render();
            surface.Presenter.BeginFind();
            surface.Refresh("foo");
            Assert.IsFalse(original.SequenceEqual(surface.Render()), "The adorner must actually render Find highlights.");
            Assert.IsTrue(surface.Editor.Selection.IsEmpty);
            surface.Presenter.EndFind();
            CollectionAssert.AreEqual(original, surface.Render());
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Rich-text search test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Surface : IDisposable
    {
        private readonly HwndSource _source;
        private readonly AdornerDecorator _root;
        internal Surface(string text, double width = 320)
        {
            _source = new HwndSource(new HwndSourceParameters("Noted rich-text search tests")
            { Width = (int)width, Height = 180, WindowStyle = unchecked((int)0x80000000) });
            Editor = new RichTextBox { FontFamily = new FontFamily("Consolas"), FontSize = 14,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };
            new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text = text;
            _root = new AdornerDecorator { Width = width, Height = 180, Child = Editor };
            Presenter = new RichTextBoxSearchPresenter(Editor);
            Session.QueryOrOptionsChanged += Presenter.ClearPendingManualNavigationAnchor;
            _source.RootVisual = _root;
            Pump();
            Editor.IsUndoEnabled = false;
            Editor.IsUndoEnabled = true;
        }
        internal RichTextBox Editor { get; }
        internal RichTextBoxSearchPresenter Presenter { get; }
        internal TextSearchSession Session { get; } = new();
        internal void Select(int start, int length)
        {
            var snapshot = new RichTextSearchSnapshot(Editor.Document);
            var pointer = snapshot.GetStart(start);
            Editor.Selection.Select(pointer, length == 0 ? pointer : snapshot.GetEnd(start + length - 1));
        }
        internal void Refresh(string query)
        {
            Presenter.Refresh(Session, query, default);
            Presenter.Present(Session, false);
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
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)_root.Width, (int)_root.Height,
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(_root);
            var pixels = new byte[(int)_root.Width * (int)_root.Height * 4];
            bitmap.CopyPixels(pixels, (int)_root.Width * 4, 0);
            return pixels;
        }
        public void Dispose()
        {
            Session.QueryOrOptionsChanged -= Presenter.ClearPendingManualNavigationAnchor;
            Presenter.Dispose();
            _source.Dispose();
        }
    }
}
