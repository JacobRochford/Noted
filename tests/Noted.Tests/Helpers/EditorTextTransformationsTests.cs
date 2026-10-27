using System.Globalization;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class EditorTextTransformationsTests
{
    [TestMethod]
    [DataRow("Uppercase", "iİIı é🙂", "IİIı É🙂")]
    [DataRow("Lowercase", "IİIı É🙂", "iİiı é🙂")]
    [DataRow("ToggleCase", "aB éÉ 𐐀𐐨🙂!", "Ab Éé 𐐨𐐀🙂!")]
    public void CaseCommandsUseInvariantRulesAndChangeOnlySelectedText(string operationName, string selected, string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                var editor = new TextBox { Text = "before " + selected + " after" };
                using var surface = new WpfEditorTestSurface(editor);
                editor.Select(7, selected.Length);
                var original = editor.Text;
                Assert.IsTrue(EditorTextTransformations.Apply(editor, Enum.Parse<TextTransformation>(operationName)));
                Assert.AreEqual("before " + expected + " after", editor.Text);
                Assert.AreEqual(7, editor.SelectionStart);
                Assert.AreEqual(expected, editor.SelectedText);
                if (selected != expected) AssertUndoRedo(editor, original, editor.Text);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
    }

    [TestMethod]
    [DataRow("SortAscending", "b\r\nA\r\na\r\nB", "A\r\nB\r\na\r\nb")]
    [DataRow("SortDescending", "b\nA\na\nB", "b\na\nB\nA")]
    [DataRow("RemoveDuplicates", " a\na\n a\nA\na", " a\na\nA")]
    [DataRow("RemoveDuplicates", "\r\n\r\na\r\na", "\r\na")]
    [DataRow("TrimTrailingWhitespace", "  a \t\r\nb\t \r\n \t", "  a\r\nb\r\n")]
    [DataRow("JoinLines", "  one \t\r\n \t\r\n  two\t\r\n three  ", "  one two three  ")]
    [DataRow("SortAscending", "z\r\na\nb", "a\r\nb\nz")]
    public void LineCommandsKeepBoundariesSeparatorsAndOneUndoUnit(string operationName, string selected, string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            var original = "before\n" + selected + "\nafter";
            var editor = new TextBox { Text = original, AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(7, selected.Length + 1);
            Assert.IsTrue(EditorTextTransformations.Apply(editor, Enum.Parse<TextTransformation>(operationName)));
            Assert.AreEqual("before\n" + expected + "\nafter", editor.Text);
            Assert.AreEqual(7, editor.SelectionStart);
            Assert.AreEqual(expected + "\n", editor.SelectedText);
            AssertUndoRedo(editor, original, editor.Text);
        });
    }

    [TestMethod]
    public void PartialLineSelectionsAffectOnlyIntersectingLinesAndSelectTheResult() => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = "outside\nzebra\napple\noutside", AcceptsReturn = true };
        using var surface = new WpfEditorTestSurface(editor);
        editor.Select(10, 6);
        Assert.IsTrue(EditorTextTransformations.Apply(editor, TextTransformation.SortAscending));
        Assert.AreEqual("outside\napple\nzebra\noutside", editor.Text);
        Assert.AreEqual("apple\nzebra", editor.SelectedText);
        Assert.IsTrue(EditorTextTransformations.Apply(editor, TextTransformation.SortDescending));
        Assert.AreEqual("outside\nzebra\napple\noutside", editor.Text);
    });

    [TestMethod]
    public void EmptySelectionsReadOnlyAndNoOpCommandsDoNotCreateEdits() => WpfTestHost.Invoke(() =>
    {
        foreach (var operation in Enum.GetValues<TextTransformation>())
        {
            var editor = new TextBox { Text = "test\nline", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.IsUndoEnabled = false;
            editor.IsUndoEnabled = true;
            editor.Select(2, 0);
            Assert.IsFalse(EditorTextTransformations.Apply(editor, operation));
            Assert.AreEqual(2, editor.CaretIndex);
            editor.SelectAll();
            editor.IsReadOnly = true;
            Assert.IsFalse(EditorTextTransformations.Apply(editor, operation));
            editor.IsReadOnly = false;
            editor.IsEnabled = false;
            Assert.IsFalse(EditorTextTransformations.Apply(editor, operation));
            Assert.AreEqual("test\nline", editor.Text);
            Assert.IsFalse(editor.CanUndo);
        }
        var unchanged = new TextBox { Text = "already lower" };
        using var unchangedSurface = new WpfEditorTestSurface(unchanged);
        unchanged.IsUndoEnabled = false;
        unchanged.IsUndoEnabled = true;
        unchanged.SelectAll();
        Assert.IsTrue(EditorTextTransformations.Apply(unchanged, TextTransformation.Lowercase));
        Assert.IsFalse(unchanged.CanUndo);
        Assert.AreEqual("already lower", unchanged.SelectedText);
        unchanged.Text = "  single line  ";
        unchanged.SelectAll();
        Assert.IsTrue(EditorTextTransformations.Apply(unchanged, TextTransformation.JoinLines));
        Assert.AreEqual("  single line  ", unchanged.Text);
        var empty = new TextBox();
        Assert.IsFalse(EditorTextTransformations.Apply(empty, TextTransformation.JoinLines));
    });

    [TestMethod]
    public void MenusUseExplicitEditorTargetsAndCommandsRespectReadOnlyAndEmptySelections() => WpfTestHost.Invoke(() =>
    {
        var editor = new TextBox { Text = "test" };
        using var surface = new WpfEditorTestSurface(editor);
        EditorTextWrapping.Attach(editor);
        EditorJsonFormatting.Attach(editor, () => new());
        EditorTextTransformations.Attach(editor);
        var command = EditorTextTransformations.Commands[TextTransformation.Uppercase];
        Assert.IsFalse(command.CanExecute(null, editor));
        editor.SelectAll();
        var group = editor.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Transform Text"));
        foreach (var item in group.Items.OfType<MenuItem>())
        {
            Assert.AreSame(editor, item.CommandTarget);
            Assert.AreEqual(0, ((System.Windows.Input.RoutedCommand)item.Command).InputGestures.Count);
        }
        Assert.IsTrue(command.CanExecute(null, editor));
        command.Execute(null, editor);
        Assert.AreEqual("TEST", editor.Text);
        editor.IsReadOnly = true;
        Assert.IsFalse(command.CanExecute(null, editor));
        Assert.IsFalse(EditorTextWrapping.Strikethrough.CanExecute(null, editor));
        editor.IsReadOnly = false;
        Assert.IsTrue(EditorTextWrapping.Strikethrough.CanExecute(null, editor));
        EditorTextWrapping.Strikethrough.Execute(null, editor);
        Assert.AreEqual("~~TEST~~", editor.Text);
        EditorTextWrapping.Strikethrough.Execute(null, editor);
        Assert.AreEqual("TEST", editor.Text);
    });

    private static void AssertUndoRedo(TextBox editor, string original, string expected)
    {
        Assert.IsTrue(editor.Undo());
        Assert.AreEqual(original, editor.Text);
        Assert.IsTrue(editor.Redo());
        Assert.AreEqual(expected, editor.Text);
    }
}
