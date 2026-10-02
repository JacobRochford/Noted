using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Models;
using Noted.Search;
using Noted.Services;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class IndentationConversionTests
{
    [TestMethod]
    [DataRow(false, false, IndentationMode.Spaces)]
    [DataRow(false, true, IndentationMode.Spaces)]
    [DataRow(true, false, IndentationMode.Spaces)]
    [DataRow(true, true, IndentationMode.Spaces)]
    [DataRow(false, false, IndentationMode.Tabs)]
    [DataRow(false, true, IndentationMode.Tabs)]
    [DataRow(true, false, IndentationMode.Tabs)]
    [DataRow(true, true, IndentationMode.Tabs)]
    public void ConversionPreservesCaretOrSelectionAndIsOneNativeUndoUnit(bool rich, bool selected, IndentationMode target)
    {
        WpfTestHost.Invoke(() =>
        {
            var original = target == IndentationMode.Spaces
                ? "\tfirst\tinside\r\n \t  second  value"
                : "    first\tinside\r\n      second  value";
            var expected = target == IndentationMode.Spaces
                ? "    first\tinside\r\n      second  value"
                : "\tfirst\tinside\r\n\t  second  value";
            var editor = CreateEditor(rich, original);
            using var surface = new WpfEditorTestSurface(editor);
            var document = (editor as RichTextBox)?.Document;
            var start = original.IndexOf("second", StringComparison.Ordinal);
            Select(editor, start, selected ? 6 : 0);
            Assert.IsFalse(editor.CanUndo);

            Assert.IsTrue(EditorIndentation.ConvertIndentation(editor, new(), target));

            Assert.AreEqual(expected + (rich ? "\r\n" : ""), ReadText(editor));
            Assert.AreEqual(selected ? "second" : "", SelectedText(editor));
            Assert.AreEqual(expected.IndexOf("second", StringComparison.Ordinal), SelectionStart(editor));
            Assert.IsTrue(editor.CanUndo);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(original + (rich ? "\r\n" : ""), ReadText(editor));
            Assert.AreEqual(start, SelectionStart(editor));
            Assert.AreEqual(selected ? "second" : "", SelectedText(editor));
            Assert.IsFalse(editor.CanUndo, "The entire document conversion must be one undo unit.");
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual(expected + (rich ? "\r\n" : ""), ReadText(editor));
            Assert.AreEqual(expected.IndexOf("second", StringComparison.Ordinal), SelectionStart(editor));
            Assert.AreEqual(selected ? "second" : "", SelectedText(editor));
            if (rich) Assert.AreSame(document, ((RichTextBox)editor).Document);
        });
    }

    [TestMethod]
    public void RichConversionPreservesFormattingAndBackwardSelectionAcrossParagraphs()
    {
        WpfTestHost.Invoke(() =>
        {
            var first = new Run("first") { FontWeight = FontWeights.Bold };
            var second = new Run("second") { FontStyle = FontStyles.Italic };
            var firstParagraph = new Paragraph();
            firstParagraph.Inlines.Add(new Run("    "));
            firstParagraph.Inlines.Add(first);
            var secondParagraph = new Paragraph();
            secondParagraph.Inlines.Add(new Run("      "));
            secondParagraph.Inlines.Add(second);
            var document = new FlowDocument(firstParagraph);
            document.Blocks.Add(secondParagraph);
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            editor.Selection.Select(second.ContentEnd, first.ContentStart);

            EditorIndentation.ConvertIndentation(editor, new(), IndentationMode.Tabs);

            Assert.AreSame(document, editor.Document);
            Assert.AreEqual("\tfirst\r\n\t  second\r\n", ReadText(editor));
            Assert.AreEqual("first\r\n\t  second", editor.Selection.Text);
            Assert.AreEqual(0, editor.CaretPosition.CompareTo(editor.Selection.Start));
            Assert.AreEqual(FontWeights.Bold, first.FontWeight);
            Assert.AreEqual(FontStyles.Italic, second.FontStyle);
            Assert.AreSame(first, firstParagraph.Inlines.LastInline);
            Assert.AreSame(second, secondParagraph.Inlines.LastInline);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("    first\r\n      second\r\n", ReadText(editor));
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("\tfirst\r\n\t  second\r\n", ReadText(editor));
        });
    }

    [TestMethod]
    public void RichConversionDoesNotTreatEmbeddedObjectsAsIndentation()
    {
        WpfTestHost.Invoke(() =>
        {
            var image = new Image { Width = 10, Height = 10 };
            var embedded = new InlineUIContainer(image);
            var paragraph = new Paragraph(new Run("    "));
            paragraph.Inlines.Add(embedded);
            var alignedText = new Run("    text");
            paragraph.Inlines.Add(alignedText);
            var editor = new RichTextBox(new FlowDocument(paragraph));
            using var surface = new WpfEditorTestSurface(editor);
            var searchText = new RichTextSearchSnapshot(editor.Document).Text;
            Assert.AreEqual("         text", searchText);
            Assert.AreEqual("    \uFFFC    text",
                new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true).Text);

            EditorIndentation.ConvertIndentation(editor, new(), IndentationMode.Tabs);

            Assert.AreSame(image, embedded.Child);
            Assert.AreSame(paragraph, embedded.Parent);
            Assert.AreEqual("    text", alignedText.Text);
            Assert.AreEqual("\t     text", new RichTextSearchSnapshot(editor.Document).Text);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(searchText, new RichTextSearchSnapshot(editor.Document).Text);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConversionPreservesOlderUndoHistoryAndDetectionDoesNotEdit(bool rich)
    {
        WpfTestHost.Invoke(() =>
        {
            const string original = "\tfirst\r\n\tsecond";
            var editor = CreateEditor(rich, original);
            using var surface = new WpfEditorTestSurface(editor);
            Select(editor, original.Length, 0);
            editor.BeginChange();
            try
            {
                if (editor is TextBox plain) plain.SelectedText = "!";
                else ((RichTextBox)editor).Selection.Text = "!";
            }
            finally { editor.EndChange(); }
            var before = ReadText(editor);
            var selected = SelectedText(editor);
            var start = SelectionStart(editor);
            var changes = 0;
            editor.TextChanged += (_, _) => changes++;

            var detected = EditorIndentation.Detect(editor, new());

            Assert.AreEqual(IndentationMode.Tabs, detected.Mode);
            Assert.AreEqual(before, ReadText(editor));
            Assert.AreEqual(selected, SelectedText(editor));
            Assert.AreEqual(start, SelectionStart(editor));
            Assert.AreEqual(0, changes);
            Assert.IsTrue(editor.CanUndo);
            EditorIndentation.ConvertIndentation(editor, new(), IndentationMode.Spaces);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(before, ReadText(editor));
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(original + (rich ? "\r\n" : ""), ReadText(editor));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NoOpAndReadOnlyConversionPreserveTextSelectionAndUndoStack(bool rich)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = CreateEditor(rich, "    text");
            using var surface = new WpfEditorTestSurface(editor);
            Select(editor, 4, 4);
            var before = ReadText(editor);
            Assert.IsTrue(EditorIndentation.ConvertIndentation(editor, new(), IndentationMode.Spaces));
            Assert.IsFalse(editor.CanUndo);
            editor.IsReadOnly = true;
            Assert.IsFalse(EditorIndentation.ConvertIndentation(editor, new(), IndentationMode.Tabs));
            Assert.AreEqual(before, ReadText(editor));
            Assert.AreEqual("text", SelectedText(editor));
            Assert.AreEqual(4, SelectionStart(editor));
            Assert.IsFalse(editor.CanUndo);
        });
    }

    [TestMethod]
    public void FooterCommandsPersistOnlyLocalSettingsAndDetectionDoesNotChangeText()
    {
        WpfTestHost.Invoke(() =>
        {
            using var directory = new TemporaryTestDirectory();
            var settings = new AppSettingsService(directory.Path);
            var defaults = settings.LoadDefaultIndentation();
            var local = defaults;
            var editor = new TextBox { Text = "\tfirst\r\n\tsecond" };
            var button = new Button { Focusable = false };
            using var surface = new WpfEditorTestSurface(editor, button);
            EditorIndentation.AttachIndicator(button, () => local, value =>
            {
                local = value;
                settings.SaveMiniPadWindowState(new() { Indentation = value });
            }, () => editor);
            void Invoke(string label)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                try
                {
                    button.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                finally { button.ContextMenu.IsOpen = false; }
            }

            editor.Select(1, 5);
            Invoke("Detect Indentation from Content");
            Assert.AreEqual("Tabs: 4", button.Content);
            Assert.AreEqual("\tfirst\r\n\tsecond", editor.Text);
            Assert.AreEqual("first", editor.SelectedText);
            Assert.IsFalse(editor.CanUndo);
            Assert.AreEqual(defaults, settings.LoadDefaultIndentation());
            Assert.AreEqual(local, new AppSettingsService(directory.Path).LoadMiniPadWindowState().Indentation);

            Invoke("Convert Indentation to Spaces");
            Assert.AreEqual("Spaces: 4", button.Content);
            Assert.AreEqual("    first\r\n    second", editor.Text);
            Assert.AreEqual("first", editor.SelectedText);
            Assert.AreEqual(defaults, settings.LoadDefaultIndentation());
            Assert.AreEqual(local, new AppSettingsService(directory.Path).LoadMiniPadWindowState().Indentation);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("\tfirst\r\n\tsecond", editor.Text);
        });
    }

    [TestMethod]
    public void FooterDetectionUsesTheCurrentEditorWithoutChangingAnotherEditorsState()
    {
        WpfTestHost.Invoke(() =>
        {
            var first = new TextBox { Text = "  first\r\n    nested" };
            var second = new TextBox { Text = "\tsecond\r\n\t\tnested" };
            var firstSettings = new IndentationSettings();
            var secondSettings = new IndentationSettings { TabSize = 8 };
            var current = first;
            var button = new Button { Focusable = false };
            using var firstSurface = new WpfEditorTestSurface(first, button);
            using var secondSurface = new WpfEditorTestSurface(second);
            EditorIndentation.AttachIndicator(button,
                () => ReferenceEquals(current, first) ? firstSettings : secondSettings,
                value =>
                {
                    if (ReferenceEquals(current, first)) firstSettings = value;
                    else secondSettings = value;
                }, () => current);
            current = second;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                button.ContextMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "Detect Indentation from Content"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            finally { button.ContextMenu.IsOpen = false; }
            Assert.AreEqual(new IndentationSettings(), firstSettings);
            Assert.AreEqual(new IndentationSettings { Mode = IndentationMode.Tabs, TabSize = 8 }, secondSettings);
            Assert.AreEqual("  first\r\n    nested", first.Text);
            Assert.AreEqual("\tsecond\r\n\t\tnested", second.Text);
        });
    }

    private static TextBoxBase CreateEditor(bool rich, string text)
    {
        if (!rich) return new TextBox { Text = text, AcceptsReturn = true, AcceptsTab = true };
        var document = new FlowDocument();
        foreach (var line in text.Split("\r\n", StringSplitOptions.None))
            document.Blocks.Add(new Paragraph(new Run(line)));
        return new RichTextBox(document);
    }

    private static string ReadText(TextBoxBase editor) => editor is TextBox plain
        ? plain.Text
        : new TextRange(((RichTextBox)editor).Document.ContentStart, ((RichTextBox)editor).Document.ContentEnd).Text;

    private static string SelectedText(TextBoxBase editor) => editor is TextBox plain
        ? plain.SelectedText : ((RichTextBox)editor).Selection.Text;

    private static int SelectionStart(TextBoxBase editor) => editor is TextBox plain
        ? plain.SelectionStart : new RichTextSearchSnapshot(((RichTextBox)editor).Document).GetOffset(((RichTextBox)editor).Selection.Start);

    private static void Select(TextBoxBase editor, int start, int length)
    {
        if (editor is TextBox plain)
        {
            plain.Select(start, length);
            return;
        }
        var rich = (RichTextBox)editor;
        var snapshot = new RichTextSearchSnapshot(rich.Document);
        TextPointer Position(int offset) => offset < snapshot.Text.Length
            ? snapshot.GetStart(offset) : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        rich.Selection.Select(Position(start), Position(start + length));
    }
}
