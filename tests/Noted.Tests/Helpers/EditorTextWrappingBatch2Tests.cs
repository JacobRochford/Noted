using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class EditorTextWrappingBatch2Tests
{
    [TestMethod]
    [DataRow(Key.Q, "\"", "\"")]
    [DataRow(Key.D9, "(", ")")]
    public void DelimiterShortcutsPreserveSelectionWhitespaceAndUndo(Key key, string opening, string closing) => WpfTestHost.Invoke(() =>
    {
        const string inner = " \tté🙂&* ";
        const string original = "before " + inner + " after";
        var editor = new TextBox { Text = original };
        using var surface = new WpfEditorTestSurface(editor);
        EditorTextWrapping.Attach(editor);
        editor.Select(7, inner.Length);
        Assert.IsTrue(KeyboardTestInput.Press(editor, key, ModifierKeys.Control | ModifierKeys.Shift).Handled);
        Assert.AreEqual("before " + opening + inner + closing + " after", editor.Text);
        Assert.AreEqual(8, editor.SelectionStart);
        Assert.AreEqual(inner, editor.SelectedText);
        var wrapped = editor.Text;
        AssertUndoRedo(editor, original, wrapped);
        KeyboardTestInput.Press(editor, key, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.AreEqual(original, editor.Text);
        Assert.AreEqual(7, editor.SelectionStart);
        Assert.AreEqual(inner, editor.SelectedText);
        AssertUndoRedo(editor, wrapped, original);
        editor.Select(7, 0);
        KeyboardTestInput.Press(editor, key, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.AreEqual("before " + opening + closing + inner + " after", editor.Text);
        Assert.AreEqual(8, editor.CaretIndex);
        Assert.AreEqual(0, editor.SelectionLength);
        KeyboardTestInput.Press(editor, key, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.AreEqual(original, editor.Text);
        Assert.AreEqual(7, editor.CaretIndex);
    });

    [TestMethod]
    [DataRow("\"test\"", Key.Q)]
    [DataRow("(test)", Key.D9)]
    public void SelectedDelimitersAreRemoved(string original, Key key) => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = original };
        editor.SelectAll();
        Assert.IsTrue(EditorTextWrapping.HandleKey(editor, key, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.AreEqual("test", editor.Text);
        Assert.AreEqual("test", editor.SelectedText);
    });

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void FencesExpandToLogicalLinesExcludeEndBoundaryAndToggleAsOneEdit(string newline) => WpfTestHost.Invoke(() =>
    {
        var prefix = "before" + newline;
        var inner = "  alpha\t" + newline + " beta" + newline;
        var original = prefix + inner + "after";
        var editor = new TextBox { Text = original, AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        EditorTextWrapping.Attach(editor);
        editor.Select(prefix.Length + 2, inner.Length - 2);
        Assert.IsTrue(KeyboardTestInput.Press(editor, Key.M, ModifierKeys.Control | ModifierKeys.Shift).Handled);
        var wrapped = prefix + "```" + newline + inner + "```" + newline + "after";
        Assert.AreEqual(wrapped, editor.Text);
        Assert.AreEqual(prefix.Length + 2 + 3 + newline.Length, editor.SelectionStart);
        Assert.AreEqual(inner[2..], editor.SelectedText);
        AssertUndoRedo(editor, original, wrapped);
        Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
        Assert.AreEqual(original, editor.Text);
        Assert.AreEqual(prefix.Length + 2, editor.SelectionStart);
        Assert.AreEqual(inner[2..], editor.SelectedText);
        AssertUndoRedo(editor, wrapped, original);
    });

    [TestMethod]
    [DataRow("", 0, "```\r\n\r\n```", 5)]
    [DataRow("one\n\nlast", 4, "one\n```\n\n```\nlast", 8)]
    [DataRow("ab", 1, "a\r\n```\r\n\r\n```\r\nb", 8)]
    public void EmptySelectionInsertsFenceLinesAndPositionsCaret(string original, int caret, string expected, int expectedCaret) => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = original, AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.CaretIndex = caret;
        Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
        Assert.AreEqual(expected, editor.Text);
        Assert.AreEqual(expectedCaret, editor.CaretIndex);
        Assert.AreEqual(0, editor.SelectionLength);
        AssertUndoRedo(editor, original, expected);
        Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
        Assert.IsFalse(editor.Text.Contains("```", StringComparison.Ordinal));
    });

    [TestMethod]
    [DataRow("```\na\n```", "a")]
    [DataRow("  ```\r\n  a\r\n  ```\r\n", "  a\r\n")]
    [DataRow("```\n\n```", "")]
    [DataRow("```\n```", "")]
    public void SelectingWholeBareFenceRemovesOnlyFenceLines(string original, string expected) => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = original, AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.SelectAll();
        Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
        Assert.AreEqual(expected, editor.Text);
        Assert.AreEqual(expected, editor.SelectedText);
        AssertUndoRedo(editor, original, expected);
    });

    [TestMethod]
    public void FenceMetadataAndNestedFencesRemainUntouched() => WpfTestHost.Invoke(() =>
    {
        foreach (var opening in new[] { "```csharp", "``` {#example}", "````", "  ```csharp" })
        {
            var editor = new TextBox { Text = opening + "\ncode\nmiddle\nlast\n```", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(opening.Length + 1, 4);
            AssertUnchanged(editor);
            editor.Select(opening.Length + 6, 6);
            AssertUnchanged(editor);
            editor.SelectAll();
            AssertUnchanged(editor);
        }
        var nested = new TextBox { Text = "before\n```\ninside\n```\nafter", AcceptsReturn = true };
        using var nestedSurface = new WpfEditorTestSurface(nested);
        nested.SelectAll();
        AssertUnchanged(nested);

        static void AssertUnchanged(TextBox editor)
        {
            var original = editor.Text;
            var start = editor.SelectionStart;
            var length = editor.SelectionLength;
            editor.IsUndoEnabled = false;
            editor.IsUndoEnabled = true;
            Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
            Assert.AreEqual(original, editor.Text);
            Assert.AreEqual(start, editor.SelectionStart);
            Assert.AreEqual(length, editor.SelectionLength);
            Assert.IsFalse(editor.CanUndo);
        }
    });

    [TestMethod]
    public void FenceRemovalKeepsInteriorSelectionsAndMixedNewlines() => WpfTestHost.Invoke(() =>
    {
        const string original = "before\r\n```\nalpha\r\nbeta\ngamma\r\n```\nafter";
        var editor = new TextBox { Text = original, AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.Select(original.IndexOf("beta", StringComparison.Ordinal) + 1, 2);
        Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
        Assert.AreEqual("before\r\nalpha\r\nbeta\ngamma\nafter", editor.Text);
        Assert.AreEqual("et", editor.SelectedText);
        Assert.AreEqual(editor.Text.IndexOf("beta", StringComparison.Ordinal) + 1, editor.SelectionStart);
        AssertUndoRedo(editor, original, editor.Text);
    });

    [TestMethod]
    public void RemovingFenceAtClosingLineKeepsTheCaretCollapsed() => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = "```\ncode\n```\nafter", AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.CaretIndex = 10;
        Assert.IsTrue(EditorTextWrapping.ToggleCodeFence(editor));
        Assert.AreEqual("code\nafter", editor.Text);
        Assert.AreEqual(4, editor.CaretIndex);
        Assert.AreEqual(0, editor.SelectionLength);
    });

    [TestMethod]
    public void NewShortcutsRespectReadOnlyExactModifiersAndNativeRichText() => WpfTestHost.Invoke(() =>
    {
        foreach (var key in new[] { Key.Q, Key.D9, Key.M })
        {
            var editor = new TextBox { Text = "test", IsReadOnly = true };
            editor.SelectAll();
            Assert.IsFalse(EditorTextWrapping.HandleKey(editor, key, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("test", editor.Text);
            editor.IsReadOnly = false;
            foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Shift,
                ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt, ModifierKeys.None })
                Assert.IsFalse(EditorTextWrapping.HandleKey(editor, key, modifiers));
            var rich = new RichTextBox(new FlowDocument(new Paragraph(new Run("rich"))));
            rich.SelectAll();
            Assert.IsFalse(EditorTextWrapping.HandleKey(rich, key, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.AreEqual("rich\r\n", rich.Selection.Text);
        }
        var multiline = new TextBox { Text = "a\nb", AcceptsReturn = true };
        multiline.SelectAll();
        Assert.IsFalse(EditorTextWrapping.HandleKey(multiline, Key.Q, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.IsFalse(EditorTextWrapping.HandleKey(multiline, Key.D9, ModifierKeys.Control | ModifierKeys.Shift));
        multiline.IsReadOnly = true;
        Assert.IsFalse(EditorTextWrapping.ToggleCodeFence(multiline));
        Assert.AreEqual("a\nb", multiline.Text);
    });

    private static void AssertUndoRedo(TextBox editor, string original, string expected)
    {
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(original, editor.Text);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(expected, editor.Text);
    }
}
