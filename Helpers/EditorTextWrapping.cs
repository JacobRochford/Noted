using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Search;

namespace Noted.Helpers;

internal static class EditorTextWrapping
{
    internal static RoutedCommand Strikethrough { get; } = new();

    internal static void Attach(TextBoxBase editor)
    {
        editor.PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled)
                e.Handled = HandleKey(editor, e.Key, e.KeyboardDevice.Modifiers);
        };
        if (editor is TextBox plain)
            plain.CommandBindings.Add(new CommandBinding(Strikethrough,
                (_, e) => { e.Handled = true; Wrap(plain, "~~", "~~"); },
                (_, e) =>
                {
                    e.CanExecute = plain.IsEnabled && !plain.IsReadOnly && plain.IsVisible &&
                        plain.SelectedText.AsSpan().IndexOfAny('\r', '\n') < 0;
                    e.Handled = true;
                }));
    }

    internal static void AddMenuItem(ItemsControl menu, Func<TextBox> getEditor)
    {
        var item = new MenuItem { Header = "Strikethrough", Command = Strikethrough, CommandTarget = getEditor() };
        if (menu is ContextMenu) item.Style = getEditor().TryFindResource("NotedContextMenuItem") as Style;
        menu.Items.Add(item);
        if (menu is ContextMenu context) context.Opened += (_, _) => item.CommandTarget = getEditor();
        else if (menu is MenuItem parent) parent.SubmenuOpened += (_, _) => item.CommandTarget = getEditor();
    }

    internal static bool HandleKey(TextBoxBase editor, Key key, ModifierKeys modifiers)
    {
        if (editor.IsReadOnly || !editor.IsEnabled)
            return false;

        if (key == Key.E && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            return Wrap(editor, "`", "`");

        if (editor is TextBox plain && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            return key switch
            {
                Key.Q => Wrap(plain, "\"", "\""),
                Key.D9 => Wrap(plain, "(", ")"),
                Key.M => ToggleCodeFence(plain),
                _ => false
            };

        if (editor is not TextBox || modifiers != ModifierKeys.Control)
            return false;

        return key switch
        {
            Key.B => Wrap(editor, "**", "**"),
            Key.I => Wrap(editor, "*", "*"),
            Key.K => Wrap(editor, "[", "](url)"),
            _ => false
        };
    }

    internal static bool Wrap(TextBoxBase editor, string opening, string closing)
    {
        if (editor.IsReadOnly || !editor.IsEnabled)
            return false;
        if (string.IsNullOrEmpty(opening) || string.IsNullOrEmpty(closing))
            throw new ArgumentException("Wrapping delimiters cannot be empty.");

        if (editor is TextBox plain)
            return WrapPlainText(plain, opening, closing);
        if (editor is RichTextBox rich)
            return WrapRichText(rich, opening, closing);
        return false;
    }

    internal static bool ToggleCodeFence(TextBox editor)
    {
        if (editor.IsReadOnly || !editor.IsEnabled) return false;
        var text = editor.Text;
        var selectionStart = editor.SelectionStart;
        var selectionEnd = selectionStart + editor.SelectionLength;
        var lines = new TextLineMap(text);
        var (first, last) = lines.SelectedLines(selectionStart, selectionEnd);
        string Line(int line) => text[lines.Starts[line]..lines.ContentEnd(line)];
        bool IsFence(int line) => Line(line).TrimStart(' ', '\t').StartsWith("```", StringComparison.Ordinal);

        var opening = -1;
        var closing = -1;
        for (var line = 0; line < lines.Starts.Length; line++)
        {
            if (!IsFence(line)) continue;
            var candidate = line;
            var fence = Line(candidate).TrimStart(' ', '\t');
            var markerLength = fence.TakeWhile(character => character == '`').Count();
            var indentation = Line(candidate)[..Line(candidate).IndexOf('`')];
            for (line++; line < lines.Starts.Length; line++)
            {
                var trimmed = Line(line).Trim(' ', '\t');
                if (trimmed.Length >= markerLength && trimmed.All(character => character == '`') &&
                    Line(line).StartsWith(indentation + "`", StringComparison.Ordinal)) break;
            }
            if (last < candidate || first > line) continue;
            // partial or unclosed fences cannot be wrapped safely with another triple-backtick pair.
            if (first < candidate || last > line || line == lines.Starts.Length) return true;
            opening = candidate;
            closing = line;
            break;
        }
        if (opening >= 0)
        {
            // metadata belongs to the fence; only bare matching fences can be removed.
            if (Line(opening).Trim(' ', '\t') != "```" || Line(closing).Trim(' ', '\t') != "```" ||
                Line(opening)[..Line(opening).IndexOf('`')] != Line(closing)[..Line(closing).IndexOf('`')])
                return true;
            if (Enumerable.Range(opening + 1, closing - opening - 1).Any(IsFence)) return true;
            var innerStart = lines.End(opening);
            var innerEnd = closing > opening + 1 ? lines.ContentEnd(closing - 1) : innerStart;
            var inner = text[innerStart..innerEnd];
            var start = lines.Starts[opening];
            var end = lines.ContentEnd(closing);
            var newStart = start + Math.Clamp(selectionStart - innerStart, 0, inner.Length);
            var limit = inner.Length + lines.End(closing) - end;
            var newEnd = editor.SelectionLength == 0 ? newStart
                : start + Math.Clamp(selectionEnd - innerStart, 0, limit);
            Replace(start, end - start, inner, newStart, newEnd - newStart);
            return true;
        }

        var newline = text[lines.ContentEnd(last)..lines.End(last)];
        if (newline.Length == 0 && first > 0)
            newline = text[lines.ContentEnd(first - 1)..lines.Starts[first]];
        if (newline.Length == 0) newline = Environment.NewLine;
        if (editor.SelectionLength == 0)
        {
            var prefix = selectionStart == lines.Starts[first] ? "" : newline;
            var suffix = selectionStart == lines.ContentEnd(first) ? "" : newline;
            var insertion = prefix + "```" + newline + newline + "```" + suffix;
            Replace(selectionStart, 0, insertion, selectionStart + prefix.Length + 3 + newline.Length, 0);
            return true;
        }
        // nested triple-backtick fences need a longer delimiter, which this command does not generate.
        if (Enumerable.Range(first, last - first + 1).Any(IsFence)) return true;
        var blockStart = lines.Starts[first];
        var blockEnd = lines.ContentEnd(last);
        var block = text[blockStart..blockEnd];
        var wrapped = "```" + newline + block + newline + "```";
        Replace(blockStart, block.Length, wrapped, selectionStart + 3 + newline.Length,
            Math.Min(selectionEnd - selectionStart, blockEnd + newline.Length - selectionStart));
        return true;

        void Replace(int start, int length, string replacement, int newStart, int newLength)
        {
            editor.BeginChange();
            try
            {
                editor.Select(start, length);
                editor.SelectedText = replacement;
                editor.Select(newStart, newLength);
            }
            finally { editor.EndChange(); }
        }
    }

    private static bool WrapPlainText(TextBox editor, string opening, string closing)
    {
        var text = editor.Text;
        var start = editor.SelectionStart;
        var length = editor.SelectionLength;
        if (!TryPlan(text, start, length, opening, closing, out var plan))
            return false;

        editor.BeginChange();
        try
        {
            editor.Select(plan.Start, plan.Length);
            var inner = text.Substring(plan.InnerStart, plan.InnerLength);
            editor.SelectedText = plan.Remove ? inner : opening + inner + closing;
            editor.Select(plan.Start + (plan.Remove ? 0 : opening.Length), plan.InnerLength);
        }
        finally
        {
            editor.EndChange();
        }
        return true;
    }

    private static bool WrapRichText(RichTextBox editor, string opening, string closing)
    {
        var snapshot = new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true);
        var start = snapshot.GetOffset(editor.Selection.Start);
        var length = snapshot.GetOffset(editor.Selection.End) - start;
        if (!TryPlan(snapshot.Text, start, length, opening, closing, out var plan))
            return false;

        editor.BeginChange();
        try
        {
            if (plan.Remove)
            {
                Delete(plan.Start + plan.Length - closing.Length, closing.Length);
                Delete(plan.Start, opening.Length);
            }
            else
            {
                Insert(plan.Start + plan.Length, closing);
                Insert(plan.Start, opening);
            }

            snapshot = new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true);
            var selectionStart = plan.Start + (plan.Remove ? 0 : opening.Length);
            editor.Selection.Select(Position(snapshot, selectionStart),
                Position(snapshot, selectionStart + plan.InnerLength));
        }
        finally
        {
            editor.EndChange();
        }
        return true;

        void Insert(int offset, string value)
        {
            var position = Position(snapshot, offset);
            new TextRange(position, position).Text = value;
            snapshot = new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true);
        }

        void Delete(int offset, int count)
        {
            new TextRange(snapshot.GetStart(offset), snapshot.GetEnd(offset + count - 1)).Text = "";
            snapshot = new RichTextSearchSnapshot(editor.Document, markStructuralWhitespace: true);
        }

        TextPointer Position(RichTextSearchSnapshot current, int offset) => offset < current.Text.Length
            ? current.GetStart(offset)
            : editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private static bool TryPlan(string text, int start, int length, string opening, string closing,
        out WrapPlan plan)
    {
        plan = default;
        if (length > 0 && text.AsSpan(start, length).IndexOfAny('\r', '\n') >= 0)
            return false;

        var total = opening.Length + closing.Length;
        if (length >= total && text.AsSpan(start, opening.Length).SequenceEqual(opening) &&
            text.AsSpan(start + length - closing.Length, closing.Length).SequenceEqual(closing))
        {
            plan = new WrapPlan(start, length, start + opening.Length, length - total, true);
            return true;
        }

        if (start >= opening.Length && start + length + closing.Length <= text.Length &&
            text.AsSpan(start - opening.Length, opening.Length).SequenceEqual(opening) &&
            text.AsSpan(start + length, closing.Length).SequenceEqual(closing))
        {
            plan = new WrapPlan(start - opening.Length, length + total, start, length, true);
            return true;
        }

        plan = new WrapPlan(start, length, start, length, false);
        return true;
    }

    private readonly record struct WrapPlan(int Start, int Length, int InnerStart, int InnerLength, bool Remove);
}
