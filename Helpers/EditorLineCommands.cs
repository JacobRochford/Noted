using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Search;

namespace Noted.Helpers;

internal static class EditorLineCommands
{
    internal static void Attach(TextBoxBase editor)
    {
        editor.PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled)
                e.Handled = HandleKey(editor, e.Key, Keyboard.Modifiers);
        };
    }

    internal static bool HandleKey(TextBoxBase editor, Key key, ModifierKeys modifiers,
        Action<IDataObject>? writeClipboard = null)
    {
        if (editor.IsReadOnly || !editor.IsEnabled)
            return false;

        var cut = key == Key.X && modifiers == ModifierKeys.Control;
        var above = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        if (!cut && !(key == Key.Enter && (modifiers == ModifierKeys.Control || above)))
            return false;

        if (cut && (editor is TextBox { SelectionLength: > 0 } ||
            editor is RichTextBox { Selection.IsEmpty: false }))
            return false; // Leave selected-text Cut, including rich formats, to WPF.

        var rich = editor as RichTextBox;
        var plain = editor as TextBox;
        var snapshot = rich is not null
            ? new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true) : null;
        var text = plain?.Text ?? snapshot!.Text;
        var caret = plain?.CaretIndex ?? snapshot!.GetOffset(rich!.CaretPosition);
        var lines = new TextLineMap(text);
        var line = lines.LineAt(caret);
        var start = lines.Starts[line];
        var next = line + 1 < lines.Starts.Length ? lines.Starts[line + 1] : text.Length;
        var end = ContentEnd(text, start, next);
        var newline = next > end ? text[end..next]
            : line > 0 ? text[ContentEnd(text, lines.Starts[line - 1], start)..start]
            : Environment.NewLine;

        TextPointer Position(int offset) => offset < snapshot!.Text.Length
            ? snapshot.GetStart(offset)
            : rich!.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);

        var editStart = above ? start : end;
        var editEnd = editStart;
        var insertion = newline;
        var newCaret = above ? start : end + newline.Length;
        if (cut)
        {
            if (text.Length == 0)
                return true;

            // The final line owns no trailing separator. Remove the preceding one,
            // but do not put that preceding separator before its clipboard text.
            editStart = next == end && line > 0
                ? ContentEnd(text, lines.Starts[line - 1], start) : start;
            editEnd = next;
            insertion = "";
            newCaret = Math.Min(start, text.Length - (editEnd - editStart));
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, start == next ? newline : text[start..next]);
            if (rich is not null && start < next)
            {
                var range = new TextRange(Position(start), Position(next));
                // Use the actual range text, not structural markers from the snapshot.
                data.SetData(DataFormats.UnicodeText, range.Text);
                using var stream = new MemoryStream();
                range.Save(stream, DataFormats.Rtf);
                data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(stream.ToArray()));
            }
            try
            {
                if (writeClipboard is not null) writeClipboard(data);
                else Clipboard.SetDataObject(data, true);
            }
            catch (ExternalException)
            {
                // A busy clipboard must not delete text or move the selection.
                return true;
            }
        }

        editor.BeginChange();
        try
        {
            if (plain is not null)
            {
                plain.Select(editStart, editEnd - editStart);
                plain.SelectedText = insertion;
                plain.Select(newCaret, 0);
            }
            else
            {
                rich!.Selection.Select(Position(editStart), Position(editEnd));
                rich.Selection.Text = insertion;
                var updated = new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true);
                rich.CaretPosition = newCaret < updated.Text.Length
                    ? updated.GetStart(newCaret)
                    : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            }
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    private static int ContentEnd(string text, int start, int next)
    {
        var end = next;
        if (end > start && text[end - 1] == '\n') end--;
        if (end > start && text[end - 1] == '\r') end--;
        return end;
    }
}
