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
public sealed class EditorTextWrappingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BackticksWrapAndToggleSelectionAsOneUndoUnit(bool rich)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = CreateEditor(rich, "before test after");
            using var surface = new WpfEditorTestSurface(editor);
            Select(editor, 7, 4);

            Assert.IsTrue(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("before `test` after", ReadText(editor));
            AssertSelection(editor, 8, 4);

            Assert.IsTrue(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("before test after", ReadText(editor));
            AssertSelection(editor, 7, 4);

            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("before `test` after", ReadText(editor));
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("before test after", ReadText(editor));
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("before `test` after", ReadText(editor));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmptySelectionPlacesCaretBetweenBackticks(bool rich)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = CreateEditor(rich, "test");
            using var surface = new WpfEditorTestSurface(editor);
            Select(editor, 2, 0);

            Assert.IsTrue(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("te``st", ReadText(editor));
            AssertSelection(editor, 3, 0);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("test", ReadText(editor));
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("te``st", ReadText(editor));
        });
    }

    [TestMethod]
    public void WrappingPreservesWhitespaceAndSpecialCharacters()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = "a  *[x]  b", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(1, 7);

            Assert.IsTrue(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("a`  *[x] ` b", editor.Text);
            Assert.AreEqual("  *[x] ", editor.SelectedText);
        });
    }

    [TestMethod]
    public void SelectedDelimitersAlsoToggleAndDifferentDelimitersWork()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = "(test)" };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(0, 6);

            Assert.IsTrue(EditorTextWrapping.Wrap(editor, "(", ")"));
            Assert.AreEqual("test", editor.Text);
            AssertSelection(editor, 0, 4);
            Assert.IsTrue(EditorTextWrapping.Wrap(editor, "(", ")"));
            Assert.AreEqual("(test)", editor.Text);
            AssertSelection(editor, 1, 4);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MultilineSelectionAndReadOnlyEditorsRemainUnchanged(bool rich)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = CreateEditor(rich, "one\r\ntwo");
            using var surface = new WpfEditorTestSurface(editor);
            Select(editor, 0, 8);
            Assert.IsFalse(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("one\r\ntwo", ReadText(editor));

            editor.IsReadOnly = true;
            Select(editor, 0, 3);
            Assert.IsFalse(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("one\r\ntwo", ReadText(editor));
        });
    }

    [TestMethod]
    public void MarkdownShortcutsKeepRichTextCommandsNative()
    {
        WpfTestHost.Invoke(() =>
        {
            var plain = new TextBox { Text = "test" };
            using var plainSurface = new WpfEditorTestSurface(plain);
            plain.SelectAll();
            Assert.IsTrue(EditorTextWrapping.HandleKey(plain, Key.B, ModifierKeys.Control));
            Assert.AreEqual("**test**", plain.Text);
            Assert.IsTrue(EditorTextWrapping.HandleKey(plain, Key.B, ModifierKeys.Control));
            Assert.AreEqual("test", plain.Text);
            Assert.IsTrue(EditorTextWrapping.HandleKey(plain, Key.I, ModifierKeys.Control));
            Assert.AreEqual("*test*", plain.Text);
            Assert.IsTrue(EditorTextWrapping.HandleKey(plain, Key.I, ModifierKeys.Control));
            Assert.AreEqual("test", plain.Text);
            Assert.IsTrue(EditorTextWrapping.HandleKey(plain, Key.K, ModifierKeys.Control));
            Assert.AreEqual("[test](url)", plain.Text);
            AssertSelection(plain, 1, 4);

            var rich = CreateEditor(true, "test");
            using var richSurface = new WpfEditorTestSurface(rich);
            Select(rich, 0, 4);
            Assert.IsFalse(EditorTextWrapping.HandleKey(rich, Key.B, ModifierKeys.Control));
            Assert.IsFalse(EditorTextWrapping.HandleKey(rich, Key.I, ModifierKeys.Control));
            Assert.IsFalse(EditorTextWrapping.HandleKey(rich, Key.K, ModifierKeys.Control));
            Assert.AreEqual("test", ReadText(rich));
        });
    }

    [TestMethod]
    public void RichWrappingKeepsFormattingInsideSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            var run = new Run("test") { FontWeight = FontWeights.Bold };
            var editor = new RichTextBox(new FlowDocument(new Paragraph(run)));
            using var surface = new WpfEditorTestSurface(editor);
            editor.Selection.Select(run.ContentStart, run.ContentEnd);

            Assert.IsTrue(EditorTextWrapping.HandleKey(editor, Key.E, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("`test`", ReadText(editor));
            Assert.AreEqual(FontWeights.Bold,
                editor.Selection.GetPropertyValue(TextElement.FontWeightProperty));
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("test", ReadText(editor));
        });
    }

    [TestMethod]
    [DataRow("before test after", 7, 4, "before **test** after", 9, 4)]
    [DataRow("**test**", 2, 4, "test", 0, 4)]
    [DataRow("**test**", 0, 8, "test", 0, 4)]
    [DataRow("ab", 1, 0, "a****b", 3, 0)]
    [DataRow("  *[x]  ", 0, 8, "**  *[x]  **", 2, 8)]
    public void BoldPreservesInnerTextSelectionAndOneUndoUnit(string original, int start, int length,
        string expected, int newStart, int newLength)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = original, AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(start, length);
            EditorTextWrapping.Attach(editor);
            Assert.IsTrue(KeyboardTestInput.Press(editor, Key.B, ModifierKeys.Control).Handled);
            Assert.AreEqual(expected, editor.Text);
            AssertSelection(editor, newStart, newLength);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(original, editor.Text);
            Assert.IsFalse(editor.CanUndo);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual(expected, editor.Text);
        });
    }

    [TestMethod]
    public void BoldLeavesReadOnlyAndMultilineContentUntouched()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = "one\ntwo", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.SelectAll();
            Assert.IsFalse(EditorTextWrapping.HandleKey(editor, Key.B, ModifierKeys.Control));
            Assert.AreEqual("one\ntwo", editor.Text);
            editor.Select(0, 3);
            editor.IsReadOnly = true;
            Assert.IsFalse(EditorTextWrapping.HandleKey(editor, Key.B, ModifierKeys.Control));
            Assert.AreEqual("one\ntwo", editor.Text);
            Assert.IsFalse(editor.CanUndo);
        });
    }

    private static TextBoxBase CreateEditor(bool rich, string text)
    {
        if (!rich)
            return new TextBox { Text = text, AcceptsReturn = true };

        var document = new FlowDocument();
        foreach (var line in text.Split("\r\n", StringSplitOptions.None))
            document.Blocks.Add(new Paragraph(new Run(line)));
        return new RichTextBox(document);
    }

    private static string ReadText(TextBoxBase editor) => editor is TextBox plain
        ? plain.Text : new RichTextSearchSnapshot(((RichTextBox)editor).Document).Text;

    private static void Select(TextBoxBase editor, int start, int length)
    {
        if (editor is TextBox plain)
        {
            plain.Select(start, length);
            return;
        }

        var rich = (RichTextBox)editor;
        var snapshot = new RichTextSearchSnapshot(rich.Document);
        TextPointer Position(int offset) => offset < snapshot.Text.Length ? snapshot.GetStart(offset)
            : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        rich.Selection.Select(Position(start), Position(start + length));
    }

    private static void AssertSelection(TextBoxBase editor, int start, int length)
    {
        if (editor is TextBox plain)
        {
            Assert.AreEqual(start, plain.SelectionStart);
            Assert.AreEqual(length, plain.SelectionLength);
            return;
        }

        var rich = (RichTextBox)editor;
        var snapshot = new RichTextSearchSnapshot(rich.Document);
        Assert.AreEqual(start, snapshot.GetOffset(rich.Selection.Start));
        Assert.AreEqual(length, snapshot.GetOffset(rich.Selection.End) - start);
    }
}
