using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Search;

namespace Noted.Helpers;

internal static class EditorNavigation
{
    internal static RoutedCommand GoToLine { get; } = new();

    internal static void Attach(TextBoxBase editor, Action toggleWordWrap)
    {
        editor.CommandBindings.Add(new CommandBinding(GoToLine,
            (_, e) => { ShowGoToLine(editor); e.Handled = true; },
            (_, e) => { e.CanExecute = editor.IsEnabled && editor.IsVisible; e.Handled = true; }));
        if (editor.ContextMenu is not null) AddMenuItem(editor.ContextMenu, () => editor);
        editor.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (e.Handled || !editor.IsEnabled) return;
            if (key == Key.Z && e.KeyboardDevice.Modifiers == ModifierKeys.Alt)
            {
                toggleWordWrap();
                e.Handled = true;
            }
            else if (key == Key.G && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                ShowGoToLine(editor);
                e.Handled = true;
            }
        };
    }

    internal static void AddMenuItem(ItemsControl menu, Func<TextBoxBase> getEditor)
    {
        var item = new MenuItem { Header = "Go to Line...", InputGestureText = "Ctrl+G", Command = GoToLine,
            CommandTarget = getEditor() };
        if (menu is ContextMenu) item.Style = getEditor().TryFindResource("NotedContextMenuItem") as Style;
        menu.Items.Add(item);
        if (menu is ContextMenu context) context.Opened += (_, _) => item.CommandTarget = getEditor();
        else if (menu is MenuItem parent) parent.SubmenuOpened += (_, _) => item.CommandTarget = getEditor();
    }

    internal static void ShowGoToLine(TextBoxBase editor)
    {
        if (!editor.IsEnabled || !editor.IsVisible) return;
        var (text, caret) = GetTextAndCaret(editor);
        var lines = new TextLineMap(text);
        var dialog = new GoToLineDialog(editor, lines.LineAt(caret) + 1, lines.Starts.Length)
        {
            Owner = Window.GetWindow(editor)
        };
        dialog.ShowDialog();
        editor.Focus();
    }

    internal static bool TryGoToLine(TextBoxBase editor, string input, out string? error)
    {
        var (text, _) = GetTextAndCaret(editor);
        var lines = new TextLineMap(text);
        error = $"Enter a line number from 1 to {lines.Starts.Length}.";
        if (!editor.IsEnabled || !int.TryParse(input.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var line) ||
            line < 1 || line > lines.Starts.Length) return false;
        var offset = lines.Starts[line - 1];
        editor.Focus();
        if (editor is TextBox plain)
        {
            plain.Select(offset, 0);
            plain.ScrollToLine(plain.GetLineIndexFromCharacterIndex(offset));
        }
        else if (editor is RichTextBox rich)
        {
            var snapshot = new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true);
            var position = offset < snapshot.Text.Length ? snapshot.GetStart(offset)
                : rich.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            rich.Selection.Select(position, position);
            position.Paragraph?.BringIntoView();
        }
        error = null;
        return true;
    }

    private static (string Text, int Caret) GetTextAndCaret(TextBoxBase editor)
    {
        if (editor is TextBox plain) return (plain.Text, plain.CaretIndex);
        var rich = (RichTextBox)editor;
        var snapshot = new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true);
        return (snapshot.Text, snapshot.GetOffset(rich.CaretPosition));
    }
}
