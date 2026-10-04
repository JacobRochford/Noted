using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Models;
using Noted.Search;
using Noted.Services;

namespace Noted.Helpers;

internal static class EditorJsonFormatting
{
    internal static RoutedCommand FormatJson { get; } = new();
    internal static RoutedCommand MinifyJson { get; } = new();

    internal static void Attach(TextBoxBase editor, Func<IndentationSettings?> getSettings)
    {
        if (editor.ContextMenu is null)
        {
            var menu = new ContextMenu();
            if (editor.TryFindResource("NotedContextMenu") is Style style) menu.Style = style;
            foreach (var (label, command) in new[]
            {
                ("Undo", ApplicationCommands.Undo), ("Redo", ApplicationCommands.Redo),
                ("Cut", ApplicationCommands.Cut), ("Copy", ApplicationCommands.Copy),
                ("Paste", ApplicationCommands.Paste), ("Select All", ApplicationCommands.SelectAll)
            })
                menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = editor,
                    Style = editor.TryFindResource("NotedContextMenuItem") as Style });
            editor.ContextMenu = menu;
        }
        AddMenuItems(editor.ContextMenu, () => editor);
        Bind(FormatJson, false);
        Bind(MinifyJson, true);
        void Bind(RoutedCommand command, bool minify)
        {
            editor.CommandBindings.Add(new CommandBinding(command, (_, e) =>
            {
                e.Handled = true;
                if (getSettings() is not { } settings) return;
                if (!TryApply(editor, minify, settings, out var error) && error is not null)
                    AppDialog.Show(Window.GetWindow(editor), error, minify ? "Minify JSON" : "Format JSON",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }, (_, e) =>
            {
                e.CanExecute = editor.IsEnabled && !editor.IsReadOnly && getSettings() is not null;
                e.Handled = true;
            }));
        }
    }

    internal static void AddMenuItems(ItemsControl menu, Func<TextBoxBase> getEditor)
    {
        menu.Items.Add(new Separator());
        foreach (var (label, command) in new[] { ("Format JSON", FormatJson), ("Minify JSON", MinifyJson) })
        {
            var item = new MenuItem { Header = label, Command = command, CommandTarget = getEditor() };
            if (menu is ContextMenu) item.Style = getEditor().TryFindResource("NotedContextMenuItem") as Style;
            menu.Items.Add(item);
            void Refresh() => item.CommandTarget = getEditor();
            if (menu is ContextMenu context) context.Opened += (_, _) => Refresh();
            else if (menu is MenuItem parent) parent.SubmenuOpened += (_, _) => Refresh();
        }
    }

    internal static bool TryApply(TextBoxBase editor, bool minify, IndentationSettings settings, out string? error)
    {
        error = null;
        if (!editor.IsEnabled || editor.IsReadOnly) return false;
        var plain = editor as TextBox;
        var rich = editor as RichTextBox;
        if (plain is null && rich is null) return false;
        var snapshot = rich is null ? null : new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true);
        var text = plain?.Text ?? snapshot!.Text;
        var selected = plain is not null ? plain.SelectionLength > 0 : !rich!.Selection.IsEmpty;
        var start = selected ? plain?.SelectionStart ?? snapshot!.GetOffset(rich!.Selection.Start) : 0;
        var end = selected ? plain is not null ? start + plain.SelectionLength : snapshot!.GetOffset(rich!.Selection.End) : text.Length;
        var caret = plain?.CaretIndex ?? snapshot!.GetOffset(rich!.CaretPosition);
        var backward = rich is not null && selected && rich.CaretPosition.CompareTo(rich.Selection.Start) == 0;
        if (rich is not null)
        {
            var rangeStart = selected ? rich.Selection.Start : rich.Document.ContentStart;
            var rangeEnd = selected ? rich.Selection.End : rich.Document.ContentEnd;
            if (HasUnsupportedStructure(rangeStart, rangeEnd))
            {
                error = "JSON formatting requires text paragraphs without lists, tables or embedded objects. Text was not changed.";
                return false;
            }
        }
        if (!JsonFormatting.TryGetEdits(text[start..end], minify, settings,
            JsonFormatting.GetNewline(text, Environment.NewLine), out var edits, out error)) return false;
        if (edits.Count == 0) return true;
        var newEnd = end + edits.Sum(edit => edit.Replacement.Length - edit.Length);
        var newCaret = start + JsonFormatting.MapOffset(edits, caret - start);
        var vertical = editor.VerticalOffset;
        var horizontal = editor.HorizontalOffset;
        editor.BeginChange();
        try
        {
            foreach (var edit in edits.Reverse())
            {
                if (plain is not null)
                {
                    plain.Select(start + edit.Start, edit.Length);
                    plain.SelectedText = edit.Replacement;
                }
                else
                {
                    TextPointer Position(int offset) => offset < text.Length ? snapshot!.GetStart(offset)
                        : rich!.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                    var editStart = start + edit.Start;
                    var editEnd = editStart + edit.Length;
                    new TextRange(Position(editStart), Position(editEnd)).Text = edit.Replacement;
                }
            }
            if (plain is not null)
                plain.Select(selected ? start : newCaret, selected ? newEnd - start : 0);
            else
            {
                var updated = new RichTextSearchSnapshot(rich!.Document, markStructuralWhitespace: true);
                TextPointer Position(int offset) => offset < updated.Text.Length ? updated.GetStart(offset)
                    : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                if (selected) rich.Selection.Select(Position(backward ? newEnd : start), Position(backward ? start : newEnd));
                else rich.CaretPosition = Position(newCaret);
            }
        }
        finally { editor.EndChange(); }
        editor.ScrollToVerticalOffset(vertical);
        editor.ScrollToHorizontalOffset(horizontal);
        return true;
    }

    private static bool HasUnsupportedStructure(TextPointer start, TextPointer end)
    {
        for (var parent = start.Parent as FrameworkContentElement; parent is not null; parent = parent.Parent as FrameworkContentElement)
            if (parent is List or Table or InlineUIContainer or BlockUIContainer) return true;
        for (var position = start; position is not null && position.CompareTo(end) < 0;
            position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement) return true;
            if (position.GetAdjacentElement(LogicalDirection.Forward) is List or Table or InlineUIContainer or BlockUIContainer) return true;
        }
        return false;
    }
}
