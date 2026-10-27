using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Search;

namespace Noted.Helpers;

internal static class EditorTextWrapping
{
    internal static void Attach(TextBoxBase editor)
    {
        editor.PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled)
                e.Handled = HandleKey(editor, e.Key, e.KeyboardDevice.Modifiers);
        };
    }

    internal static bool HandleKey(TextBoxBase editor, Key key, ModifierKeys modifiers)
    {
        if (editor.IsReadOnly || !editor.IsEnabled)
            return false;

        if (key == Key.E && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            return Wrap(editor, "`", "`");

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
