using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Search;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class EditorLinePasteTests
{
    [TestMethod]
    [DataRow("the whole sentence", 7, "the whole sentence\r\nthe whole sentence", 27)]
    [DataRow("first\r\nlast", 2, "first\r\nfirst\r\nlast", 9)]
    [DataRow("first\r\nlast", 9, "first\r\nlast\r\nlast", 15)]
    [DataRow("", 0, "\r\n", 2)]
    [DataRow("first\r\n", 7, "first\r\n\r\n", 9)]
    public void LineCopyPastesBelowTheCurrentLineAndFollowsTheCopy(string original, int caret, string expected, int expectedCaret) =>
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                SetCaret(editor, caret);
                var data = CopyLine(editor);
                Assert.IsTrue(data.GetDataPresent(EditorLineCommands.LineCopyFormat, autoConvert: false));
                Assert.IsTrue(EditorLineCommands.TryPasteLine(editor, data));
                Assert.AreEqual(expected, ReadText(editor));
                Assert.AreEqual(expectedCaret, Caret(editor));
                AssertUndoRedo(editor, original, expected);
            }
        });

    [TestMethod]
    [DataRow("first\nlast", 8, "first\nlast\nlast")]
    [DataRow("first\nlast", 2, "first\nfirst\nlast")]
    [DataRow("first\rlast", 8, "first\rlast\rlast")]
    public void PlainLinePasteKeepsDestinationNewlines(string original, int caret, string expected) => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = original, AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.CaretIndex = caret;
        Assert.IsTrue(EditorLineCommands.TryPasteLine(editor, CopyLine(editor)));
        Assert.AreEqual(expected, editor.Text);
        AssertUndoRedo(editor, original, expected);
    });

    [TestMethod]
    public void RepeatedPastesAndCrossEditorCopiesUseTheClipboardLineRatherThanTheDestination() => WpfTestHost.Invoke(() =>
    {
        var source = new TextBox { Text = "copied line", CaretIndex = 4 };
        var data = CopyLine(source);
        var target = new TextBox { Text = "destination\nnext", AcceptsReturn = true, CaretIndex = 3 };
        using var surface = new WpfEditorTestSurface(target);
        Assert.IsTrue(EditorLineCommands.TryPasteLine(target, data));
        Assert.AreEqual("destination\ncopied line\nnext", target.Text);
        Assert.AreEqual(15, target.CaretIndex);
        Assert.IsTrue(EditorLineCommands.TryPasteLine(target, data));
        Assert.AreEqual("destination\ncopied line\ncopied line\nnext", target.Text);
        Assert.AreEqual(27, target.CaretIndex);
    });

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void NativePasteRecognizesTheClipboardMarkerAndUndoesAsOneEdit(bool rich) => WithClipboard(() =>
    {
        const string original = "middle of a sentence";
        var editor = CreateEditor(rich, original);
        using var surface = new WpfEditorTestSurface(editor);
        if (editor is TextBox plain) PlainTextPaste.Attach(plain, () => false);
        EditorLineCommands.Attach(editor);
        SetCaret(editor, 8);
        Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.C, ModifierKeys.Control));
        Assert.IsTrue(Clipboard.GetDataObject()!.GetDataPresent(EditorLineCommands.LineCopyFormat, autoConvert: false));
        editor.Paste();
        const string expected = original + "\r\n" + original;
        Assert.AreEqual(expected, ReadText(editor));
        Assert.AreEqual(original.Length + 2 + 8, Caret(editor));
        AssertUndoRedo(editor, original, expected);
    });

    [TestMethod]
    [DoNotParallelize]
    public void LinePasteKeepsNormalizationSettingsAndTheExistingBypassCommand() => WithClipboard(() =>
    {
        const string original = "a “quote” line";
        var source = new TextBox { Text = original };
        Clipboard.SetDataObject(CopyLine(source), true);
        foreach (var normalize in new[] { false, true })
        {
            var target = new TextBox { Text = "target", AcceptsReturn = true, CaretIndex = 3 };
            using var surface = new WpfEditorTestSurface(target);
            PlainTextPaste.Attach(target, () => normalize);
            EditorLineCommands.Attach(target);
            target.Paste();
            Assert.AreEqual("target\r\n" + (normalize ? "a \"quote\" line" : original), target.Text);
            Assert.IsTrue(target.Undo());
            target.CaretIndex = 3;
            PlainTextPaste.PasteWithoutNormalization.Execute(null, target);
            Assert.AreEqual("target\r\n" + original, target.Text);
        }
    });

    [TestMethod]
    [DoNotParallelize]
    public void OrdinaryClipboardTextAndSelectedPasteKeepNativeInsertionBehavior() => WithClipboard(() =>
    {
        var editor = new TextBox { Text = "before after", AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        PlainTextPaste.Attach(editor, () => false);
        EditorLineCommands.Attach(editor);
        Clipboard.SetText("ordinary");
        editor.CaretIndex = 7;
        editor.Paste();
        Assert.AreEqual("before ordinaryafter", editor.Text);

        Clipboard.SetDataObject(CopyLine(new TextBox { Text = "copied" }), true);
        editor.Text = "before REMOVE after";
        editor.Select(7, 6);
        editor.Paste();
        Assert.AreEqual("before copied\r\n after", editor.Text);
        editor.SelectAll();
        Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.C, ModifierKeys.Control));
        editor.Copy();
        Assert.IsFalse(Clipboard.GetDataObject()!.GetDataPresent(EditorLineCommands.LineCopyFormat));
    });

    [TestMethod]
    public void ReadOnlyDisabledAndSelectedEditorsDoNotUseLinePaste() => WpfTestHost.Invoke(() =>
    {
        var data = CopyLine(new TextBox { Text = "copied" });
        foreach (var rich in new[] { false, true })
        {
            var editor = CreateEditor(rich, "original");
            using var surface = new WpfEditorTestSurface(editor);
            editor.IsReadOnly = true;
            Assert.IsFalse(EditorLineCommands.TryPasteLine(editor, data));
            editor.IsReadOnly = false;
            editor.IsEnabled = false;
            Assert.IsFalse(EditorLineCommands.TryPasteLine(editor, data));
            editor.IsEnabled = true;
            editor.SelectAll();
            Assert.IsFalse(EditorLineCommands.TryPasteLine(editor, data));
            Assert.AreEqual("original", ReadText(editor));
        }
    });

    [TestMethod]
    public void RichLinePasteRetainsFormattingAndSurroundingParagraphObjects() => WpfTestHost.Invoke(() =>
    {
        var copiedParagraph = new Paragraph(new Run("formatted") { FontWeight = FontWeights.Bold });
        var source = new RichTextBox(new FlowDocument(copiedParagraph));
        using var sourceSurface = new WpfEditorTestSurface(source);
        var data = CopyLine(source);
        var original = new Paragraph(new Run("destination"));
        var following = new Paragraph(new Run("following"));
        var document = new FlowDocument(original);
        document.Blocks.Add(following);
        var target = new RichTextBox(document);
        using var targetSurface = new WpfEditorTestSurface(target);
        SetCaret(target, 3);
        Assert.IsTrue(EditorLineCommands.TryPasteLine(target, data));
        Assert.AreEqual("destination\r\nformatted\r\nfollowing", ReadText(target));
        Assert.AreSame(original, document.Blocks.FirstBlock);
        Assert.AreSame(following, document.Blocks.LastBlock);
        var pasted = (Paragraph)original.NextBlock!;
        Assert.AreEqual(FontWeights.Bold, new TextRange(pasted.ContentStart, pasted.ContentEnd)
            .GetPropertyValue(TextElement.FontWeightProperty));
        AssertUndoRedo(target, "destination\r\nfollowing", "destination\r\nformatted\r\nfollowing");
    });

    [TestMethod]
    public void RichStructuralLinesRemainAvailableToNativePaste() => WpfTestHost.Invoke(() =>
    {
        var paragraph = new Paragraph(new Run("first"));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run("second"));
        var editor = new RichTextBox(new FlowDocument(paragraph));
        using var surface = new WpfEditorTestSurface(editor);
        EditorLineCommands.Attach(editor);
        var data = CopyLine(new TextBox { Text = "copied" });
        var args = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
        editor.RaiseEvent(args);
        Assert.IsFalse(args.CommandCancelled);
        Assert.AreSame(paragraph, editor.Document.Blocks.FirstBlock);
        Assert.AreEqual("first\r\nsecond", ReadText(editor));
    });

    private static IDataObject CopyLine(TextBoxBase editor)
    {
        IDataObject? data = null;
        Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.C, ModifierKeys.Control, copied => data = copied));
        Assert.IsNotNull(data);
        return data;
    }

    private static TextBoxBase CreateEditor(bool rich, string text)
    {
        if (!rich) return new TextBox { Text = text, AcceptsReturn = true };
        var document = new FlowDocument();
        foreach (var line in text.Split("\r\n")) document.Blocks.Add(new Paragraph(new Run(line)));
        return new RichTextBox(document);
    }

    private static string ReadText(TextBoxBase editor) => editor is TextBox plain ? plain.Text
        : new RichTextSearchSnapshot(((RichTextBox)editor).Document, markStructuralWhitespace: true).Text;

    private static int Caret(TextBoxBase editor) => editor is TextBox plain ? plain.CaretIndex
        : new RichTextSearchSnapshot(((RichTextBox)editor).Document, markStructuralWhitespace: true)
            .GetOffset(((RichTextBox)editor).CaretPosition);

    private static void SetCaret(TextBoxBase editor, int caret)
    {
        if (editor is TextBox plain) plain.CaretIndex = caret;
        else
        {
            var rich = (RichTextBox)editor;
            var snapshot = new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true);
            rich.CaretPosition = caret < snapshot.Text.Length ? snapshot.GetStart(caret)
                : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        }
    }

    private static void AssertUndoRedo(TextBoxBase editor, string original, string expected)
    {
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(original, ReadText(editor));
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(expected, ReadText(editor));
    }

    private static void WithClipboard(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var current = Clipboard.GetDataObject();
                DataObject? previous = null;
                if (current is not null)
                {
                    previous = new DataObject();
                    foreach (var format in current.GetFormats(autoConvert: false))
                    {
                        var value = current.GetData(format, autoConvert: false);
                        if (value is System.IO.MemoryStream stream) value = new System.IO.MemoryStream(stream.ToArray());
                        if (value is not null) previous.SetData(format, value, autoConvert: false);
                    }
                }
                try { action(); }
                finally
                {
                    if (previous is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(previous, true);
                }
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Line paste test did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
