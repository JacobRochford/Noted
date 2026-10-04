using System.Runtime.InteropServices;
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
public sealed class EditorLineCommandsTests
{
    [TestMethod]
    [DataRow("one\r\ntwo\r\nthree", 1, "two\r\nthree", "one\r\n", 0)]
    [DataRow("one\r\ntwo\r\nthree", 6, "one\r\nthree", "two\r\n", 5)]
    [DataRow("one\r\ntwo\r\nthree", 12, "one\r\ntwo", "three", 8)]
    [DataRow("only", 2, "", "only", 0)]
    [DataRow("one\r\n", 5, "one", "\r\n", 3)]
    [DataRow("one\r\n\r\nthree", 5, "one\r\nthree", "\r\n", 5)]
    public void CutHandlesLineBoundariesAndUndoesAsOneEdit(
        string original, int caret, string expected, string clipboard, int expectedCaret)
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, caret, 0);
                IDataObject? copied = null;
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control, data => copied = data));
                Assert.IsNotNull(copied);
                Assert.AreEqual(clipboard, copied.GetData(DataFormats.UnicodeText));
                Assert.AreEqual(expected, ReadText(editor));
                Assert.AreEqual(expectedCaret, Caret(editor));
                AssertUndoRedo(editor, original, expected);
            }
        });
    }

    [TestMethod]
    [DataRow("a\nb\nc", 2, "a\nc", "b\n")]
    [DataRow("a\rb\rc", 2, "a\rc", "b\r")]
    [DataRow("a\r\nb\nc", 3, "a\r\nc", "b\n")]
    public void CutPreservesPlainTextNewlines(string original, int caret, string expected, string clipboard)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = original, AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretIndex = caret;
            IDataObject? copied = null;
            EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control, data => copied = data);
            Assert.AreEqual(expected, editor.Text);
            Assert.AreEqual(clipboard, copied!.GetData(DataFormats.UnicodeText));
            AssertUndoRedo(editor, original, expected);
        });
    }

    [TestMethod]
    [DataRow("one\r\ntwo", 1, false, "one\r\n\r\ntwo", 5)]
    [DataRow("one\r\ntwo", 6, false, "one\r\ntwo\r\n", 10)]
    [DataRow("one\r\ntwo", 1, true, "\r\none\r\ntwo", 0)]
    [DataRow("one\r\ntwo", 6, true, "one\r\n\r\ntwo", 5)]
    [DataRow("", 0, false, "\r\n", 2)]
    [DataRow("", 0, true, "\r\n", 0)]
    [DataRow("one\r\n", 5, false, "one\r\n\r\n", 7)]
    [DataRow("one\r\n", 5, true, "one\r\n\r\n", 5)]
    [DataRow("  indented", 4, false, "  indented\r\n", 12)]
    public void InsertUsesLogicalLineAndUndoesAsOneEdit(
        string original, int caret, bool above, string expected, int expectedCaret)
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, caret, 0);
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.Enter,
                    ModifierKeys.Control | (above ? ModifierKeys.Shift : ModifierKeys.None)));
                Assert.AreEqual(expected, ReadText(editor));
                Assert.AreEqual(expectedCaret, Caret(editor));
                AssertUndoRedo(editor, original, expected);
            }
        });
    }

    [TestMethod]
    [DataRow("a\nb", 0, false, "a\n\nb")]
    [DataRow("a\rb", 2, false, "a\rb\r")]
    [DataRow("a\r\nb\nc", 3, true, "a\r\n\nb\nc")]
    public void InsertUsesLocalNewlineConvention(string original, int caret, bool above, string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = original, AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretIndex = caret;
            EditorLineCommands.HandleKey(editor, Key.Enter,
                ModifierKeys.Control | (above ? ModifierKeys.Shift : ModifierKeys.None));
            Assert.AreEqual(expected, editor.Text);
        });
    }

    [TestMethod]
    public void SelectedCutIsLeftToWpfAndInsertPreservesSelectedContent()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, "one\r\ntwo");
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, 1, 5);
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control,
                    _ => Assert.Fail("Selected Cut must use WPF's command.")));
                Assert.AreEqual("one\r\ntwo", ReadText(editor));
                var caret = Caret(editor);
                EditorLineCommands.HandleKey(editor, Key.Enter, ModifierKeys.Control);
                Assert.AreEqual(caret < 5 ? "one\r\n\r\ntwo" : "one\r\ntwo\r\n", ReadText(editor));
                Assert.IsTrue(editor is TextBox plain ? plain.SelectionLength == 0 : ((RichTextBox)editor).Selection.IsEmpty);
                Assert.IsTrue(editor.Undo());
                Assert.AreEqual("one\r\ntwo", ReadText(editor));
            }
        });
    }

    [TestMethod]
    public void ReadOnlyDisabledUnrelatedKeysAndClipboardFailureLeaveEditorUnchanged()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, "one");
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, 1, 0);
                editor.IsReadOnly = true;
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control));
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.Enter, ModifierKeys.Control));
                editor.IsReadOnly = false;
                editor.IsEnabled = false;
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.Enter, ModifierKeys.Control | ModifierKeys.Shift));
                editor.IsEnabled = true;
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.Enter, ModifierKeys.None));
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.Enter, ModifierKeys.Shift));
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control | ModifierKeys.Shift));
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control,
                    _ => throw new COMException("Clipboard busy")));
                Assert.AreEqual("one", ReadText(editor));
                Assert.AreEqual(1, Caret(editor));
                Assert.IsFalse(editor.CanUndo);
            }
        });
    }

    [TestMethod]
    public void RichCutPreservesFormattingAndExportsRtfAcrossSoftLineBreaks()
    {
        WpfTestHost.Invoke(() =>
        {
            var first = new Run("one") { FontWeight = FontWeights.Bold };
            var last = new Run("two") { FontStyle = FontStyles.Italic };
            var paragraph = new Paragraph(first);
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(last);
            var editor = new RichTextBox(new FlowDocument(paragraph));
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretPosition = first.ContentStart;
            IDataObject? copied = null;
            EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control, data => copied = data);
            Assert.AreEqual("two", ReadText(editor));
            Assert.AreEqual(FontStyles.Italic, new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd)
                .GetPropertyValue(TextElement.FontStyleProperty));
            Assert.IsTrue(copied!.GetDataPresent(DataFormats.Rtf));
            var rtf = (string)copied.GetData(DataFormats.Rtf);
            StringAssert.Contains(rtf, "one");
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("one\r\ntwo", ReadText(editor));
        });
    }

    [TestMethod]
    public void EmptyCutDoesNotOverwriteClipboardOrCreateUndoHistory()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, "");
                using var surface = new WpfEditorTestSurface(editor);
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.X, ModifierKeys.Control,
                    _ => Assert.Fail("There is no text to copy.")));
                Assert.AreEqual("", ReadText(editor));
                Assert.IsFalse(editor.CanUndo);
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RichInsertionPreservesExistingRunFormatting(bool above)
    {
        WpfTestHost.Invoke(() =>
        {
            var run = new Run("text") { FontWeight = FontWeights.Bold };
            var editor = new RichTextBox(new FlowDocument(new Paragraph(run)));
            using var surface = new WpfEditorTestSurface(editor);
            var document = editor.Document;
            editor.CaretPosition = run.ContentStart.GetPositionAtOffset(2)!;
            EditorLineCommands.HandleKey(editor, Key.Enter,
                ModifierKeys.Control | (above ? ModifierKeys.Shift : ModifierKeys.None));
            var snapshot = new RichTextSearchSnapshot(document);
            var start = snapshot.Text.IndexOf("text", StringComparison.Ordinal);
            Assert.AreEqual(FontWeights.Bold, new TextRange(snapshot.GetStart(start), snapshot.GetEnd(start + 3))
                .GetPropertyValue(TextElement.FontWeightProperty));
            Assert.AreSame(document, editor.Document);
            AssertUndoRedo(editor, "text", above ? "\r\ntext" : "text\r\n");
        });
    }

    [TestMethod]
    public void WrappedTextUsesLogicalRatherThanVisualLines()
    {
        WpfTestHost.Invoke(() =>
        {
            var original = new string('a', 200) + "\r\nlast";
            var editor = new TextBox { Text = original, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            using var surface = new WpfEditorTestSurface(editor);
            Assert.IsTrue(editor.LineCount > 2);
            editor.CaretIndex = 100;
            EditorLineCommands.HandleKey(editor, Key.Enter, ModifierKeys.Control);
            Assert.AreEqual(new string('a', 200) + "\r\n\r\nlast", editor.Text);
            Assert.AreEqual(202, editor.CaretIndex);
        });
    }

    private static TextBoxBase CreateEditor(bool rich, string text)
    {
        if (!rich) return new TextBox { Text = text, AcceptsReturn = true };
        var document = new FlowDocument();
        foreach (var line in text.Split("\r\n", StringSplitOptions.None))
            document.Blocks.Add(new Paragraph(new Run(line)));
        return new RichTextBox(document);
    }

    private static string ReadText(TextBoxBase editor) => editor is TextBox plain
        ? plain.Text : new RichTextSearchSnapshot(((RichTextBox)editor).Document).Text;

    private static int Caret(TextBoxBase editor) => editor is TextBox plain
        ? plain.CaretIndex : new RichTextSearchSnapshot(((RichTextBox)editor).Document).GetOffset(((RichTextBox)editor).CaretPosition);

    private static void Select(TextBoxBase editor, int start, int length)
    {
        if (editor is TextBox plain) plain.Select(start, length);
        else
        {
            var rich = (RichTextBox)editor;
            var snapshot = new RichTextSearchSnapshot(rich.Document);
            TextPointer Position(int offset) => offset < snapshot.Text.Length ? snapshot.GetStart(offset)
                : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            rich.Selection.Select(Position(start), Position(start + length));
        }
    }

    private static void AssertUndoRedo(TextBoxBase editor, string original, string expected)
    {
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(original, ReadText(editor));
        Assert.IsFalse(editor.CanUndo, "The command must be one undo unit.");
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(expected, ReadText(editor));
    }
}
