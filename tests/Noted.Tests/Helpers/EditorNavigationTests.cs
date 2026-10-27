using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Search;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class EditorNavigationTests
{
    [TestMethod]
    [DataRow("first\r\nsecond\r\n", "3", 15)]
    [DataRow("first\nsecond\n", "3", 13)]
    [DataRow("first\nsecond\r\nthird", "2", 6)]
    [DataRow("first\rsecond", "2", 6)]
    [DataRow("", "1", 0)]
    [DataRow("only", " 1 ", 0)]
    public void GoToLineUsesLogicalOneBasedLinesWithoutEditing(string text, string input, int expectedCaret) => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 50 };
        using var surface = new WpfEditorTestSurface(editor);
        editor.IsUndoEnabled = false;
        editor.IsUndoEnabled = true;
        Assert.IsTrue(EditorNavigation.TryGoToLine(editor, input, out var error), error);
        Assert.AreEqual(expectedCaret, editor.CaretIndex);
        Assert.AreEqual(0, editor.SelectionLength);
        Assert.AreEqual(text, editor.Text);
        Assert.IsFalse(editor.CanUndo);
    });

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("zero")]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("3")]
    [DataRow("1.5")]
    [DataRow("2147483648")]
    public void InvalidLineInputLeavesSelectionAndContentUntouched(string input) => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = "first\nsecond", AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.Select(1, 3);
        Assert.IsFalse(EditorNavigation.TryGoToLine(editor, input, out var error));
        Assert.IsNotNull(error);
        Assert.AreEqual(1, editor.SelectionStart);
        Assert.AreEqual(3, editor.SelectionLength);
        Assert.AreEqual("first\nsecond", editor.Text);
    });

    [TestMethod]
    public void NavigationKeepsUndoHistoryAndWorksInReadOnlyRichText() => WpfTestHost.Invoke(() =>
    {
        var plain = new TextBox { Text = "first\nsecond", AcceptsReturn = true };
        using var plainSurface = new WpfEditorTestSurface(plain);
        plain.Select(0, 0);
        plain.SelectedText = "added ";
        Assert.IsTrue(EditorNavigation.TryGoToLine(plain, "2", out _));
        Assert.IsTrue(plain.Undo());
        Assert.AreEqual("first\nsecond", plain.Text);

        var paragraph = new Paragraph(new Run("bold") { FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run("soft"));
        var document = new FlowDocument(paragraph);
        document.Blocks.Add(new Paragraph(new Run("next")));
        var rich = new RichTextBox(document) { IsReadOnly = true };
        using var richSurface = new WpfEditorTestSurface(rich);
        var original = new TextRange(document.ContentStart, document.ContentEnd).Text;
        rich.IsUndoEnabled = false;
        rich.IsUndoEnabled = true;
        Assert.IsTrue(EditorNavigation.TryGoToLine(rich, "2", out _));
        var snapshot = new RichTextSearchSnapshot(document, markStructuralWhitespace: true);
        Assert.AreEqual(snapshot.Text.IndexOf("soft", StringComparison.Ordinal), snapshot.GetOffset(rich.CaretPosition));
        Assert.IsTrue(EditorNavigation.TryGoToLine(rich, "3", out _));
        Assert.AreEqual(snapshot.Text.IndexOf("next", StringComparison.Ordinal), snapshot.GetOffset(rich.CaretPosition));
        Assert.AreSame(paragraph, document.Blocks.FirstBlock);
        Assert.AreEqual(original, new TextRange(document.ContentStart, document.ContentEnd).Text);
        Assert.IsFalse(rich.CanUndo);
    });

    [TestMethod]
    public void AltWordWrapRequiresExactModifiersAndTheAttachedEditor() => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = "text" };
        using var surface = new WpfEditorTestSurface(editor);
        var toggles = 0;
        EditorNavigation.Attach(editor, () => toggles++);
        Assert.IsTrue(KeyboardTestInput.Press(editor, Key.Z, ModifierKeys.Alt).Handled);
        Assert.AreEqual(1, toggles);
        KeyboardTestInput.Press(editor, Key.Z, ModifierKeys.Alt | ModifierKeys.Shift);
        KeyboardTestInput.Press(editor, Key.Z, ModifierKeys.Control);
        editor.IsEnabled = false;
        KeyboardTestInput.Press(editor, Key.Z, ModifierKeys.Alt);
        Assert.AreEqual(1, toggles);
        var search = new TextBox { Text = "search" };
        Assert.IsFalse(KeyboardTestInput.Press(search, Key.Z, ModifierKeys.Alt).Handled);
        Assert.IsFalse(KeyboardTestInput.Press(search, Key.G, ModifierKeys.Control).Handled);
        Assert.IsFalse(EditorNavigation.GoToLine.CanExecute(null, search));
    });
}
