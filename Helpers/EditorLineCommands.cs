using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Noted.Search;

namespace Noted.Helpers;

internal static class EditorLineCommands
{
    internal static void Attach(TextBoxBase editor)
    {
        editor.PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled)
                e.Handled = HandleKey(editor, e.Key == Key.System ? e.SystemKey : e.Key, e.KeyboardDevice.Modifiers);
        };
    }

    internal static bool HandleKey(TextBoxBase editor, Key key, ModifierKeys modifiers,
        Action<IDataObject>? writeClipboard = null)
    {
        var copy = key == Key.C && modifiers == ModifierKeys.Control;
        if (!editor.IsEnabled || editor.IsReadOnly && !copy)
            return false;

        var move = (key == Key.Up || key == Key.Down) && modifiers == ModifierKeys.Alt;
        var duplicate = (key == Key.Up || key == Key.Down) && modifiers == (ModifierKeys.Alt | ModifierKeys.Shift);
        var delete = key == Key.K && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        if (copy || move || duplicate || delete)
            return EditLines(editor, key == Key.Up, copy, move, duplicate, writeClipboard);

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
            var clipboardText = start == next ? newline : text[start..next];
            TextRange? range = null;
            if (rich is not null && start < next)
            {
                range = new TextRange(Position(start), Position(next));
                clipboardText = range.Text;
            }
            if (!TryWriteClipboard(clipboardText, range, writeClipboard))
                return true;
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

    private static bool EditLines(TextBoxBase editor, bool above, bool copy, bool move, bool duplicate,
        Action<IDataObject>? writeClipboard)
    {
        var plain = editor as TextBox;
        var rich = editor as RichTextBox;
        if (plain is null && rich is null)
            return false;
        var hasSelection = plain is not null ? plain.SelectionLength > 0 : !rich!.Selection.IsEmpty;
        if (copy && hasSelection)
            return false;

        var snapshot = rich is null ? null : new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true);
        var text = plain?.Text ?? snapshot!.Text;
        var selectionStart = plain?.SelectionStart ?? snapshot!.GetOffset(rich!.Selection.Start);
        var selectionEnd = plain is not null ? selectionStart + plain.SelectionLength : snapshot!.GetOffset(rich!.Selection.End);
        var lines = new TextLineMap(text);
        var first = lines.LineAt(selectionStart);
        var last = lines.LineAt(selectionEnd);
        if (selectionEnd > selectionStart && selectionEnd == lines.Starts[last])
            last--;
        var start = lines.Starts[first];
        var next = last + 1 < lines.Starts.Length ? lines.Starts[last + 1] : text.Length;
        var end = ContentEnd(text, lines.Starts[last], next);
        var newline = next > end ? text[end..next]
            : first > 0 ? text[ContentEnd(text, lines.Starts[first - 1], start)..start]
            : Environment.NewLine;

        if (copy)
        {
            TextRange? range = null;
            if (rich is not null && start < end)
            {
                var rangeEnd = rich.Document.ContentEnd;
                if (next > end)
                    rangeEnd = next < snapshot!.Text.Length ? snapshot.GetStart(next)
                        : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                range = new TextRange(snapshot!.GetStart(start), rangeEnd);
            }
            var clipboardText = range?.Text ?? text[start..end];
            if (!clipboardText.EndsWith('\r') && !clipboardText.EndsWith('\n'))
                clipboardText += newline;
            TryWriteClipboard(clipboardText, range, writeClipboard);
            return true;
        }
        if (move && (above ? first == 0 : last == lines.Starts.Length - 1))
            return true;
        if (!move && !duplicate && text.Length == 0)
            return true;
        if (rich is not null)
            return EditRichLines(rich, snapshot!, first, last, lines, selectionStart, selectionEnd, above, move, duplicate);

        var block = text[start..end];
        var editStart = start;
        var editEnd = end;
        var newStart = start;
        var newTrailingLength = next - end;
        string replacement;
        if (move && above)
        {
            editStart = lines.Starts[first - 1];
            var previousEnd = ContentEnd(text, editStart, start);
            replacement = block + text[previousEnd..start] + text[editStart..previousEnd];
            newStart = editStart;
            newTrailingLength = start - previousEnd;
        }
        else if (move)
        {
            editEnd = ContentEnd(text, next, last + 2 < lines.Starts.Length ? lines.Starts[last + 2] : text.Length);
            replacement = text[next..editEnd] + text[end..next] + block;
            newStart = start + editEnd - end;
            newTrailingLength = (last + 2 < lines.Starts.Length ? lines.Starts[last + 2] : text.Length) - editEnd;
        }
        else if (duplicate)
        {
            editStart = editEnd = above ? start : end;
            replacement = above ? block + newline : newline + block;
            newStart = above ? start : end + newline.Length;
            if (above) newTrailingLength = newline.Length;
        }
        else
        {
            editStart = next == end && first > 0
                ? ContentEnd(text, lines.Starts[first - 1], start) : start;
            editEnd = next;
            replacement = "";
        }

        if (editStart == editEnd && replacement.Length == 0)
            return true;
        plain!.BeginChange();
        try
        {
            plain.Select(editStart, editEnd - editStart);
            plain.SelectedText = replacement;
            if (move || duplicate)
            {
                var limit = newStart + block.Length + newTrailingLength;
                var selectedSeparator = selectionEnd > selectionStart && selectionEnd == next && next > end;
                var newEnd = selectedSeparator ? limit : Math.Min(newStart + selectionEnd - start, limit);
                var caret = Math.Min(newStart + selectionStart - start, newEnd);
                plain.Select(caret, newEnd - caret);
            }
            else
                plain.Select(Math.Min(start, text.Length - (editEnd - editStart)), 0);
        }
        finally
        {
            plain.EndChange();
        }
        return true;
    }

    private static bool EditRichLines(RichTextBox editor, RichTextSearchSnapshot snapshot, int first, int last,
        TextLineMap lines, int selectionStart, int selectionEnd, bool above, bool move, bool duplicate)
    {
        TextPointer Position(int offset) => offset < snapshot.Text.Length ? snapshot.GetStart(offset)
            : editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        var start = lines.Starts[first];
        var end = ContentEnd(snapshot.Text, lines.Starts[last],
            last + 1 < lines.Starts.Length ? lines.Starts[last + 1] : snapshot.Text.Length);
        var firstParagraph = Position(start).Paragraph;
        var lastParagraph = Position(end).Paragraph;
        var paragraphs = new List<Paragraph>();
        for (var paragraph = firstParagraph; paragraph is not null; paragraph = paragraph.NextBlock as Paragraph)
        {
            if (!IsLogicalParagraph(paragraph, editor.Document))
                return true;
            paragraphs.Add(paragraph);
            if (ReferenceEquals(paragraph, lastParagraph))
                break;
        }
        // list/table boundaries and soft line breaks need a different structural edit.
        if (paragraphs.Count != last - first + 1 || !ReferenceEquals(paragraphs.LastOrDefault(), lastParagraph))
            return true;
        var neighbor = (above ? firstParagraph!.PreviousBlock : lastParagraph!.NextBlock) as Paragraph;
        if (move && (neighbor is null || !IsLogicalParagraph(neighbor, editor.Document)))
            return true;

        var inserted = paragraphs;
        if (duplicate)
        {
            if (paragraphs.Any(paragraph => !CanDuplicate(paragraph.Inlines)))
                return true;
            using var stream = new MemoryStream();
            new TextRange(firstParagraph!.ElementStart, lastParagraph!.ElementEnd).Save(stream, DataFormats.XamlPackage);
            stream.Position = 0;
            var fragment = new FlowDocument();
            new TextRange(fragment.ContentStart, fragment.ContentEnd).Load(stream, DataFormats.XamlPackage);
            inserted = fragment.Blocks.OfType<Paragraph>().ToList();
            if (inserted.Count != paragraphs.Count || fragment.Blocks.Count != inserted.Count)
                return true;
            foreach (var paragraph in inserted)
                fragment.Blocks.Remove(paragraph);
        }

        var backward = !editor.Selection.IsEmpty && editor.CaretPosition.CompareTo(editor.Selection.Start) == 0;
        editor.BeginChange();
        try
        {
            var blocks = editor.Document.Blocks;
            if (move || duplicate)
            {
                var anchor = move ? neighbor! : above ? firstParagraph! : lastParagraph!;
                foreach (var paragraph in above ? inserted : inserted.AsEnumerable().Reverse())
                {
                    if (move) blocks.Remove(paragraph);
                    if (above) blocks.InsertBefore(anchor, paragraph);
                    else blocks.InsertAfter(anchor, paragraph);
                }
                snapshot = new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true);
                var updatedLines = new TextLineMap(snapshot.Text);
                var newLine = above ? first : last + 1;
                if (move) newLine = above ? first - 1 : first + 1;
                var newStart = updatedLines.Starts[newLine];
                var followingLine = newLine + inserted.Count;
                var limit = followingLine < updatedLines.Starts.Length
                    ? updatedLines.Starts[followingLine] : snapshot.Text.Length;
                var newEnd = Math.Min(newStart + selectionEnd - start, limit);
                var newSelectionStart = Math.Min(newStart + selectionStart - start, newEnd);
                editor.Selection.Select(Position(backward ? newEnd : newSelectionStart),
                    Position(backward ? newSelectionStart : newEnd));
            }
            else
            {
                foreach (var paragraph in paragraphs)
                    blocks.Remove(paragraph);
                if (blocks.Count == 0)
                    blocks.Add(new Paragraph());
                snapshot = new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true);
                editor.CaretPosition = Position(Math.Min(start, snapshot.Text.Length));
            }
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    private static bool IsLogicalParagraph(Paragraph paragraph, FlowDocument document) =>
        ReferenceEquals(paragraph.Parent, document) &&
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.AsSpan().IndexOfAny('\r', '\n') < 0;

    private static bool CanDuplicate(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run || inline is LineBreak)
                continue;
            if (inline is InlineUIContainer container && container.Child is Image image && image.Source is BitmapSource)
                continue;
            if (inline is not Span span || !CanDuplicate(span.Inlines))
                return false;
        }
        return true;
    }

    private static bool TryWriteClipboard(string text, TextRange? richRange, Action<IDataObject>? writeClipboard)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (richRange is not null)
        {
            using var stream = new MemoryStream();
            richRange.Save(stream, DataFormats.Rtf);
            data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(stream.ToArray()));
        }
        try
        {
            if (writeClipboard is not null) writeClipboard(data);
            else Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (ExternalException)
        {
            // a busy clipboard must not delete text or move the selection.
            return false;
        }
    }

    private static int ContentEnd(string text, int start, int next)
    {
        var end = next;
        if (end > start && text[end - 1] == '\n') end--;
        if (end > start && text[end - 1] == '\r') end--;
        return end;
    }
}
