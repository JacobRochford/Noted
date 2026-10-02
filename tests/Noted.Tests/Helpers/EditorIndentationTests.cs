using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Helpers;
using Noted.Models;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class EditorIndentationTests
{
    [TestMethod]
    public void TextBoxInsertionReplacesSelectionAndSupportsUndoRedo()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "abREMOVEz", AcceptsTab = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(2, 6);
            EditorIndentation.Insert(editor, new());
            Assert.AreEqual("ab  z", editor.Text);
            Assert.AreEqual(4, editor.CaretIndex);
            Assert.AreEqual(0, editor.SelectionLength);
            Assert.IsTrue(editor.CanUndo, "The edit must create a native WPF undo unit.");
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("abREMOVEz", editor.Text);
            Assert.IsTrue(editor.CanRedo);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("ab  z", editor.Text);
        });
    }

    [TestMethod]
    public void MultilineTextBoxIndentationExcludesLineAtSelectionEndAndUndoesTogether()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "one\r\ntwo\r\nthree", AcceptsReturn = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.Select(1, 9);
            EditorIndentation.Insert(editor, new());
            Assert.AreEqual("    one\r\n    two\r\nthree", editor.Text);
            Assert.AreEqual(5, editor.SelectionStart);
            Assert.AreEqual("ne\r\n    two\r\n", editor.SelectedText);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("one\r\ntwo\r\nthree", editor.Text);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("    one\r\n    two\r\nthree", editor.Text);
        });
    }

    [TestMethod]
    public void MultilineRichTextIndentationPreservesFormattingAndUndoesTogether()
    {
        RunSta(() =>
        {
            var first = new Run("one") { FontWeight = FontWeights.Bold };
            var second = new Run("two") { FontStyle = FontStyles.Italic };
            var document = new FlowDocument(new Paragraph(first));
            document.Blocks.Add(new Paragraph(second));
            var editor = new RichTextBox(document);
            using var surface = new WpfEditorTestSurface(editor);
            editor.Selection.Select(first.ContentStart, second.ContentEnd);
            EditorIndentation.Insert(editor, new() { Mode = IndentationMode.Tabs });
            Assert.AreEqual("\tone\r\n\ttwo\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
            Assert.AreEqual("one\r\n\ttwo", editor.Selection.Text);
            Assert.AreEqual(FontWeights.Bold, first.FontWeight);
            Assert.AreEqual(FontStyles.Italic, second.FontStyle);
            Assert.AreSame(document, editor.Document);
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("one\r\ntwo\r\n", new TextRange(document.ContentStart, document.ContentEnd).Text);
        });
    }

    [TestMethod]
    public void RichTextBoxInsertionPreservesFormattingAndSupportsUndoRedo()
    {
        RunSta(() =>
        {
            var run = new Run("abREMOVEz") { FontWeight = FontWeights.Bold };
            var editor = new RichTextBox(new FlowDocument(new Paragraph(run)));
            using var surface = new WpfEditorTestSurface(editor);
            var document = editor.Document;
            editor.Selection.Select(run.ContentStart.GetPositionAtOffset(2)!, run.ContentStart.GetPositionAtOffset(8)!);
            EditorIndentation.Insert(editor, new());
            Assert.AreEqual("ab  z\r\n", new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text);
            Assert.AreSame(document, editor.Document);
            Assert.IsTrue(editor.Selection.IsEmpty);
            Assert.AreEqual("ab  ", new TextRange(editor.Document.ContentStart, editor.CaretPosition).Text);
            Assert.AreEqual(FontWeights.Bold, new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd)
                .GetPropertyValue(TextElement.FontWeightProperty));
            Assert.IsTrue(editor.CanUndo, "The edit must create a native WPF undo unit.");
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("abREMOVEz\r\n", new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text);
            Assert.AreSame(document, editor.Document);
            Assert.IsTrue(editor.CanRedo);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual("ab  z\r\n", new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text);
            Assert.AreSame(document, editor.Document);
        });
    }

    [TestMethod]
    public void AttachedHandlerReadsCurrentDocumentSettingsAndIgnoresOtherKeys()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "a", AcceptsTab = true };
            using var surface = new WpfEditorTestSurface(editor);
            editor.CaretIndex = 1;
            var settings = new IndentationSettings();
            EditorIndentation.Attach(editor, () => settings);
            KeyEventArgs Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, surface.Source, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                editor.RaiseEvent(args);
                return args;
            }
            Assert.IsFalse(Press(Key.F3).Handled);
            Assert.AreEqual("a", editor.Text);
            Assert.IsTrue(Press(Key.Tab).Handled);
            Assert.AreEqual("a   ", editor.Text);
            settings = settings with { Mode = IndentationMode.Tabs, TabSize = 8 };
            Assert.IsTrue(Press(Key.Tab).Handled);
            Assert.AreEqual("a   \t", editor.Text);
            editor.IsReadOnly = true;
            Assert.IsFalse(Press(Key.Tab).Handled);
            Assert.AreEqual("a   \t", editor.Text);
        });
    }

    [TestMethod]
    public void ChangingFooterModePreservesTextSelectionAndUndoHistory()
    {
        RunSta(() =>
        {
            var editor = new TextBox { Text = "\ttext" };
            var button = new Button();
            using var surface = new WpfEditorTestSurface(editor, button);
            editor.Select(1, 0);
            EditorIndentation.Insert(editor, new());
            Assert.IsTrue(editor.CanUndo, "Indentation must be undoable before changing footer state.");
            editor.Select(1, 2);
            var before = editor.Text;
            var settings = new IndentationSettings();
            EditorIndentation.AttachIndicator(button, () => settings, value => settings = value, () => editor);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var tabs = (MenuItem)button.ContextMenu.Items[1];
            tabs.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            button.ContextMenu.IsOpen = false;
            Assert.AreEqual("Tabs: 4", button.Content);
            Assert.AreEqual(IndentationMode.Tabs, settings.Mode);
            Assert.AreEqual(before, editor.Text);
            Assert.AreEqual(1, editor.SelectionStart);
            Assert.AreEqual(2, editor.SelectionLength);
            Assert.IsTrue(editor.CanUndo, "The edit must create a native WPF undo unit.");
            Assert.IsTrue(editor.Undo());
            Assert.AreEqual("\ttext", editor.Text);
            Assert.IsTrue(editor.CanRedo);
            Assert.IsTrue(editor.Redo());
            Assert.AreEqual(before, editor.Text);
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Indentation test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
