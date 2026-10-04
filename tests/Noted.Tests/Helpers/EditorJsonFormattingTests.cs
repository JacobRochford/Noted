using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class EditorJsonFormattingTests
{
    [TestMethod]
    public void SelectionFormatsOnlySelectedJsonAndUndoesAsOneOperation()
    {
        RunSta(() =>
        {
            const string original = "before {\"a\":1} after";
            var editor = new TextBox { Text = original, AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(7, 7);
            Assert.IsTrue(EditorJsonFormatting.TryApply(editor, false, new() { TabSize = 2 }, out var error), error);
            Assert.AreEqual("before {\r\n  \"a\": 1\r\n} after", editor.Text);
            Assert.AreEqual(7, editor.SelectionStart);
            Assert.AreEqual("{\r\n  \"a\": 1\r\n}", editor.SelectedText);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(original, editor.Text);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("before {\r\n  \"a\": 1\r\n} after", editor.Text);
        });
    }

    [TestMethod]
    public void SelectionMinifiesOnlyTargetAndUsesDocumentNewlineWhenFormattingAgain()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "prefix\n{ \"a\" : 1 }\nsuffix", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(7, 11);
            Assert.IsTrue(EditorJsonFormatting.TryApply(editor, true, new(), out var error), error);
            Assert.AreEqual("prefix\n{\"a\":1}\nsuffix", editor.Text);
            Assert.AreEqual("{\"a\":1}", editor.SelectedText);
            Assert.IsTrue(EditorJsonFormatting.TryApply(editor, false, new() { TabSize = 2 }, out error), error);
            Assert.AreEqual("prefix\n{\n  \"a\": 1\n}\nsuffix", editor.Text);
        });
    }

    [TestMethod]
    public void AttachedMenusExposeCommandsWithExplicitEditorTargets()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "{ \"a\" : 1 }" };
            using var surface = new WpfEditorTestSurface(editor);
            EditorJsonFormatting.Attach(editor, () => new());
            var item = editor.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Minify JSON"));
            Assert.AreSame(editor, item.CommandTarget);
            Assert.IsTrue(EditorJsonFormatting.MinifyJson.CanExecute(null, editor));
            EditorJsonFormatting.MinifyJson.Execute(null, editor);
            Assert.AreEqual("{\"a\":1}", editor.Text);
            editor.IsReadOnly = true;
            Assert.IsFalse(EditorJsonFormatting.MinifyJson.CanExecute(null, editor));
        });
    }

    [TestMethod]
    public void NoSelectionMinifiesWholeDocumentAndKeepsCaretAtSameToken()
    {
        RunSta(() =>
        {
            const string original = "{ \"a\" : [ 1, 2 ] }";
            var editor = new TextBox { Text = original };
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretIndex = original.IndexOf('2');
            Assert.IsTrue(EditorJsonFormatting.TryApply(editor, true, new(), out var error), error);
            Assert.AreEqual("{\"a\":[1,2]}", editor.Text);
            Assert.AreEqual(editor.Text.IndexOf('2'), editor.CaretIndex);
            Assert.AreEqual(0, editor.SelectionLength);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(original, editor.Text);
        });
    }

    [TestMethod]
    public void InvalidSelectionLeavesTextSelectionAndUndoHistoryUnchanged()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "prefix {bad} suffix" };
            using var surface = new WpfEditorTestSurface(editor);
            editor.IsUndoEnabled = false;
            editor.IsUndoEnabled = true;
            editor.Select(7, 5);
            Assert.IsFalse(EditorJsonFormatting.TryApply(editor, false, new(), out var error));
            Assert.IsNotNull(error);
            Assert.AreEqual("prefix {bad} suffix", editor.Text);
            Assert.AreEqual(7, editor.SelectionStart);
            Assert.AreEqual(5, editor.SelectionLength);
            Assert.IsFalse(editor.CanUndo);
        });
    }

    [TestMethod]
    public void RichSelectionPreservesSurroundingAndTokenFormattingAndDocumentIdentity()
    {
        RunSta(() =>
        {
            var before = new Run("before ") { FontWeight = FontWeights.Bold };
            var json = new Run("{\"a\":[1,2]}") { FontStyle = FontStyles.Italic };
            var after = new Run(" after") { FontWeight = FontWeights.Bold };
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(before);
            paragraph.Inlines.Add(json);
            paragraph.Inlines.Add(after);
            var document = new FlowDocument(paragraph);
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            editor.Selection.Select(json.ContentStart, json.ContentEnd);
            Assert.IsTrue(EditorJsonFormatting.TryApply(editor, false, new() { TabSize = 2 }, out var error), error);
            const string formatted = "{\r\n  \"a\": [\r\n    1,\r\n    2\r\n  ]\r\n}";
            Assert.AreEqual("before " + formatted + " after\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
            Assert.AreEqual(formatted, editor.Selection.Text);
            Assert.AreSame(document, editor.Document);
            Assert.AreEqual(FontStyles.Italic, editor.Selection.GetPropertyValue(TextElement.FontStyleProperty));
            Assert.AreEqual(FontWeights.Bold, new TextRange(document.ContentStart, editor.Selection.Start).GetPropertyValue(TextElement.FontWeightProperty));
            Assert.AreEqual(FontWeights.Bold, new TextRange(editor.Selection.End, document.ContentEnd).GetPropertyValue(TextElement.FontWeightProperty));
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("before {\"a\":[1,2]} after\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("before " + formatted + " after\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
        });
    }

    [TestMethod]
    public void RichWholeDocumentMinifiesParagraphWhitespaceWithUndo()
    {
        RunSta(() =>
        {
            var document = new FlowDocument(new Paragraph(new Run("{")));
            document.Blocks.Add(new Paragraph(new Run("\"a\": 1")));
            document.Blocks.Add(new Paragraph(new Run("}")));
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            var original = new TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.IsTrue(EditorJsonFormatting.TryApply(editor, true, new(), out var error), error);
            // A FlowDocument always retains its final paragraph separator.
            Assert.AreEqual("{\"a\":1}\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
            Assert.IsTrue(editor.Selection.IsEmpty);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(original, new TextRange(document.ContentStart, document.ContentEnd).Text);
        });
    }

    [TestMethod]
    public void RichListsAreRejectedWithoutEditing()
    {
        RunSta(() =>
        {
            var document = new FlowDocument(new List(new ListItem(new Paragraph(new Run("{}")))));
            var editor = new RichTextBox(document);
            var original = new TextRange(document.ContentStart, document.ContentEnd).Text;
            Assert.IsFalse(EditorJsonFormatting.TryApply(editor, true, new(), out var error));
            StringAssert.Contains(error!, "lists, tables or embedded objects");
            Assert.AreEqual(original, new TextRange(document.ContentStart, document.ContentEnd).Text);
        });
    }

    [TestMethod]
    public void ReadOnlyEditorsIgnoreFormatting()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "{ \"a\" : 1 }", IsReadOnly = true };
            Assert.IsFalse(EditorJsonFormatting.TryApply(editor, true, new(), out _));
            Assert.AreEqual("{ \"a\" : 1 }", editor.Text);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "JSON editor test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
