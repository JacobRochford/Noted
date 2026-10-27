using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    [TestMethod]
    [DataRow("one\r\ntwo\r\nthree", 1, 0, false, "two\r\none\r\nthree", 6, 0)]
    [DataRow("one\r\ntwo\r\nthree", 11, 0, true, "one\r\nthree\r\ntwo", 6, 0)]
    [DataRow("one\r\ntwo\r\nthree", 0, 5, false, "two\r\none\r\nthree", 5, 5)]
    [DataRow("one\r\ntwo\r\nthree", 6, 8, true, "two\r\nthree\r\none", 1, 8)]
    [DataRow("one\r\ntwo\r\nthree", 0, 10, false, "three\r\none\r\ntwo", 7, 8)]
    [DataRow("one\r\n", 5, 0, true, "\r\none", 0, 0)]
    [DataRow("\r\none", 0, 0, false, "one\r\n", 5, 0)]
    public void MovePreservesLogicalLinesSelectionAndUndo(string original, int start, int length, bool above,
        string expected, int newStart, int newLength)
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, start, length);
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, above ? Key.Up : Key.Down, ModifierKeys.Alt));
                Assert.AreEqual(expected, ReadText(editor), $"rich={rich}");
                AssertSelection(editor, newStart, newLength);
                AssertUndoRedo(editor, original, expected);
            }
        });
    }

    [TestMethod]
    [DataRow("one\r\ntwo", 1, 0, true, "one\r\none\r\ntwo", 1, 0)]
    [DataRow("one\r\ntwo", 6, 0, false, "one\r\ntwo\r\ntwo", 11, 0)]
    [DataRow("one\r\ntwo\r\nthree", 1, 9, false, "one\r\ntwo\r\none\r\ntwo\r\nthree", 11, 9)]
    [DataRow("one\r\ntwo\r\nthree", 6, 8, true, "one\r\ntwo\r\nthree\r\ntwo\r\nthree", 6, 8)]
    [DataRow("", 0, 0, false, "\r\n", 2, 0)]
    [DataRow("", 0, 0, true, "\r\n", 0, 0)]
    [DataRow("one\r\n", 5, 0, false, "one\r\n\r\n", 7, 0)]
    public void DuplicateFollowsTheCopyAndUndoesAsOneEdit(string original, int start, int length, bool above,
        string expected, int newStart, int newLength)
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, start, length);
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, above ? Key.Up : Key.Down,
                    ModifierKeys.Alt | ModifierKeys.Shift));
                Assert.AreEqual(expected, ReadText(editor), $"rich={rich}");
                AssertSelection(editor, newStart, newLength);
                AssertUndoRedo(editor, original, expected);
            }
        });
    }

    [TestMethod]
    [DataRow("one\r\ntwo\r\nthree", 6, 0, "one\r\nthree", 5)]
    [DataRow("one\r\ntwo\r\nthree", 11, 0, "one\r\ntwo", 8)]
    [DataRow("one\r\ntwo\r\nthree", 1, 9, "three", 0)]
    [DataRow("one\r\ntwo\r\nthree", 6, 9, "one", 3)]
    [DataRow("one\r\n", 5, 0, "one", 3)]
    [DataRow("\r\none", 0, 0, "one", 0)]
    [DataRow("only", 0, 4, "", 0)]
    public void DeleteRemovesSelectedLinesWithoutAnExtraBlankLine(string original, int start, int length,
        string expected, int caret)
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, start, length);
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.K, ModifierKeys.Control | ModifierKeys.Shift));
                Assert.AreEqual(expected, ReadText(editor), $"rich={rich}");
                AssertSelection(editor, caret, 0);
                AssertUndoRedo(editor, original, expected);
            }
        });
    }

    [TestMethod]
    [DataRow("a\nb\nc", 2, Key.Down, ModifierKeys.Alt, "a\nc\nb")]
    [DataRow("a\r\nb\nc", 3, Key.Up, ModifierKeys.Alt, "b\r\na\nc")]
    [DataRow(" a\nb", 1, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift, " a\n a\nb")]
    [DataRow("a\nb", 2, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift, "a\nb\nb")]
    [DataRow("a\nb", 2, Key.K, ModifierKeys.Control | ModifierKeys.Shift, "a")]
    public void NewPlainLineCommandsKeepExistingSeparators(string original, int caret, Key key,
        ModifierKeys modifiers, string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = original, AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretIndex = caret;
            EditorLineCommands.HandleKey(editor, key, modifiers);
            Assert.AreEqual(expected, editor.Text);
            AssertUndoRedo(editor, original, expected);
        });
    }

    [TestMethod]
    [DataRow("one\r\ntwo", 1, "one\r\n")]
    [DataRow("one\r\ntwo", 6, "two\r\n")]
    [DataRow("", 0, "\r\n")]
    [DataRow("one\r\n", 5, "\r\n")]
    public void CopyIncludesALineSeparatorAndDoesNotChangeTheDocument(string original, int caret, string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, original);
                using var surface = new WpfEditorTestSurface(editor);
                Select(editor, caret, 0);
                editor.IsReadOnly = true;
                IDataObject? copied = null;
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.C, ModifierKeys.Control, data => copied = data));
                Assert.AreEqual(expected, copied!.GetData(DataFormats.UnicodeText));
                Assert.AreEqual(original, ReadText(editor));
                AssertSelection(editor, caret, 0);
                Assert.IsFalse(editor.CanUndo);
                Select(editor, 0, Math.Min(original.Length, 1));
                if (original.Length > 0)
                    Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.C, ModifierKeys.Control,
                        _ => Assert.Fail("Selected Copy belongs to WPF.")));
            }
        });
    }

    [TestMethod]
    public void BoundariesReadOnlyAndExtraModifiersDoNotEdit()
    {
        WpfTestHost.Invoke(() =>
        {
            foreach (var rich in new[] { false, true })
            {
                var editor = CreateEditor(rich, "");
                using var surface = new WpfEditorTestSurface(editor);
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.Up, ModifierKeys.Alt));
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.Down, ModifierKeys.Alt));
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.K, ModifierKeys.Control | ModifierKeys.Shift));
                Assert.AreEqual("", ReadText(editor));
                Assert.IsFalse(editor.CanUndo);
                editor.IsReadOnly = true;
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.K, ModifierKeys.Control | ModifierKeys.Shift));
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift));
                editor.IsReadOnly = false;
                Assert.IsFalse(EditorLineCommands.HandleKey(editor, Key.Down, ModifierKeys.Alt | ModifierKeys.Control));
            }
        });
    }

    [TestMethod]
    public void RichMoveKeepsParagraphPropertiesAndEmbeddedElements()
    {
        WpfTestHost.Invoke(() =>
        {
            var first = new Paragraph(new Run("one")) { TextAlignment = TextAlignment.Right, Margin = new Thickness(12) };
            var run = new Run("two") { FontStyle = FontStyles.Italic, Foreground = Brushes.Red };
            var image = new Image { Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4), Width = 12, Height = 12 };
            var second = new Paragraph(run);
            second.Inlines.Add(new InlineUIContainer(image));
            var document = new FlowDocument();
            document.Blocks.Add(first);
            document.Blocks.Add(second);
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretPosition = run.ContentStart;
            var original = ReadText(editor);
            Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.Up, ModifierKeys.Alt));
            Assert.AreSame(second, document.Blocks.FirstBlock);
            Assert.AreSame(image, second.Inlines.OfType<InlineUIContainer>().Single().Child);
            Assert.AreEqual(FontStyles.Italic, run.FontStyle);
            Assert.AreEqual(TextAlignment.Right, first.TextAlignment);
            Assert.AreEqual(new Thickness(12), first.Margin);
            AssertUndoRedo(editor, original, ReadText(editor));
        });
    }

    [TestMethod]
    public void RichDuplicateKeepsFormattingParagraphPropertiesAndImages()
    {
        WpfTestHost.Invoke(() =>
        {
            var run = new Run("two") { FontWeight = FontWeights.Bold };
            var image = new Image { Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 255, 0, 255 }, 4), Width = 12, Height = 12 };
            var paragraph = new Paragraph(run) { TextAlignment = TextAlignment.Right, Margin = new Thickness(12) };
            paragraph.Inlines.Add(new InlineUIContainer(image));
            var editor = new RichTextBox(new FlowDocument(paragraph));
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretPosition = run.ContentStart;
            Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift));
            Assert.AreEqual(2, editor.Document.Blocks.Count);
            var clone = (Paragraph)editor.Document.Blocks.LastBlock!;
            Assert.AreEqual(TextAlignment.Right, clone.TextAlignment);
            Assert.AreEqual(new Thickness(12), clone.Margin);
            var snapshot = new RichTextSearchSnapshot(editor.Document);
            var offset = snapshot.Text.LastIndexOf("two", StringComparison.Ordinal);
            Assert.AreEqual(FontWeights.Bold, new TextRange(snapshot.GetStart(offset), snapshot.GetEnd(offset + 2))
                .GetPropertyValue(TextElement.FontWeightProperty));
            var clonedImage = clone.Inlines.OfType<InlineUIContainer>().Single().Child as Image;
            Assert.IsNotNull(clonedImage);
            var pixels = new byte[4];
            ((BitmapSource)clonedImage.Source).CopyPixels(pixels, 4, 0);
            CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, pixels);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual(1, editor.Document.Blocks.Count);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual(2, editor.Document.Blocks.Count);
        });
    }

    [TestMethod]
    public void RichCopyExportsRtfWithoutChangingFormatting()
    {
        WpfTestHost.Invoke(() =>
        {
            var run = new Run("bold") { FontWeight = FontWeights.Bold };
            var editor = new RichTextBox(new FlowDocument(new Paragraph(run)));
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretPosition = run.ContentStart;
            IDataObject? copied = null;
            EditorLineCommands.HandleKey(editor, Key.C, ModifierKeys.Control, data => copied = data);
            var target = new RichTextBox(new FlowDocument(new Paragraph(new Run("suffix"))));
            using var targetSurface = new WpfEditorTestSurface(target);
            var position = target.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes((string)copied!.GetData(DataFormats.Rtf)));
            var range = new TextRange(position, position);
            range.Load(stream, DataFormats.Rtf);
            Assert.AreEqual("bold\r\nsuffix", ReadText(target));
            var pasted = new RichTextSearchSnapshot(target.Document);
            Assert.AreEqual(FontWeights.Bold, new TextRange(pasted.GetStart(0), pasted.GetEnd(3))
                .GetPropertyValue(TextElement.FontWeightProperty));
            Assert.AreEqual("bold", ReadText(editor));
            Assert.IsFalse(editor.CanUndo);
        });
    }

    [TestMethod]
    public void RichStructuralLinesAreLeftIntactInsteadOfFlattened()
    {
        WpfTestHost.Invoke(() =>
        {
            var paragraph = new Paragraph(new Run("one"));
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run("two"));
            var document = new FlowDocument(paragraph);
            document.Blocks.Add(new System.Windows.Documents.List(new ListItem(new Paragraph(new Run("item")))));
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretPosition = paragraph.ContentStart;
            var original = ReadText(editor);
            foreach (var (key, modifiers) in new[] { (Key.Down, ModifierKeys.Alt),
                (Key.Down, ModifierKeys.Alt | ModifierKeys.Shift), (Key.K, ModifierKeys.Control | ModifierKeys.Shift) })
            {
                Assert.IsTrue(EditorLineCommands.HandleKey(editor, key, modifiers));
                Assert.AreEqual(original, ReadText(editor));
                Assert.AreSame(paragraph, document.Blocks.FirstBlock);
                Assert.IsFalse(editor.CanUndo);
            }
        });
    }

    [TestMethod]
    public void RoutedAltKeysAndRepeatedOperationsKeepTheCaretWithTheLine()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = "one\ntwo\nthree", AcceptsReturn = true };
            var search = new TextBox { Text = "query" };
            using var surface = new WpfEditorTestSurface(editor, search);
            EditorLineCommands.Attach(editor);
            editor.CaretIndex = 1;
            Assert.IsTrue(KeyboardTestInput.Press(editor, Key.Down, ModifierKeys.Alt).Handled);
            Assert.AreEqual("two\none\nthree", editor.Text);
            Assert.AreEqual(5, editor.CaretIndex);
            Assert.IsTrue(KeyboardTestInput.Press(editor, Key.Down, ModifierKeys.Alt).Handled);
            Assert.AreEqual("two\nthree\none", editor.Text);
            Assert.AreEqual(11, editor.CaretIndex);
            Assert.IsTrue(KeyboardTestInput.Press(editor, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift).Handled);
            Assert.AreEqual("two\nthree\none\none", editor.Text);
            Assert.AreEqual(11, editor.CaretIndex);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("two\nthree\none", editor.Text);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("two\none\nthree", editor.Text);
            KeyboardTestInput.Press(search, Key.Down, ModifierKeys.Alt);
            KeyboardTestInput.Press(search, Key.K, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.AreEqual("query", search.Text);
        });
    }

    [TestMethod]
    public void MovingAcrossMixedNewlinesKeepsSelectionOnTheNextLineBoundary()
    {
        WpfTestHost.Invoke(() =>
        {
            var editor = new TextBox { Text = "one\r\ntwo\nthree", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(5, 4);
            EditorLineCommands.HandleKey(editor, Key.Up, ModifierKeys.Alt);
            Assert.AreEqual("two\r\none\nthree", editor.Text);
            Assert.AreEqual("two\r\n", editor.SelectedText);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("one\r\ntwo\nthree", editor.Text);
        });
    }

    [TestMethod]
    public void RichDeleteKeepsAdjacentStructureAndUnsupportedDuplicatesStayUntouched()
    {
        WpfTestHost.Invoke(() =>
        {
            var first = new Paragraph(new Run("remove"));
            var button = new Button { Content = "keep" };
            var second = new Paragraph(new InlineUIContainer(button));
            var document = new FlowDocument(first);
            document.Blocks.Add(second);
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretPosition = first.ContentStart;
            EditorLineCommands.HandleKey(editor, Key.K, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.AreSame(second, document.Blocks.FirstBlock);
            Assert.AreSame(button, second.Inlines.OfType<InlineUIContainer>().Single().Child);
            Assert.IsTrue(editor.Undo());
            editor.CaretPosition = second.ContentStart;
            var original = ReadText(editor);
            Assert.IsTrue(EditorLineCommands.HandleKey(editor, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift));
            Assert.AreEqual(original, ReadText(editor));
            Assert.AreSame(button, second.Inlines.OfType<InlineUIContainer>().Single().Child);
            Assert.IsFalse(editor.CanUndo);
        });
    }

    private static void AssertSelection(TextBoxBase editor, int start, int length)
    {
        if (editor is TextBox plain)
        {
            Assert.AreEqual(start, plain.SelectionStart);
            Assert.AreEqual(length, plain.SelectionLength);
        }
        else
        {
            var rich = (RichTextBox)editor;
            var snapshot = new RichTextSearchSnapshot(rich.Document);
            Assert.AreEqual(start, snapshot.GetOffset(rich.Selection.Start));
            Assert.AreEqual(length, snapshot.GetOffset(rich.Selection.End) - start);
        }
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
