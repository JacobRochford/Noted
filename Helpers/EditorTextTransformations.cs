using System.Text;
using System.Windows.Controls;
using System.Windows.Input;

namespace Noted.Helpers;

internal enum TextTransformation
{
    Uppercase, Lowercase, ToggleCase, SortAscending, SortDescending, RemoveDuplicates, TrimTrailingWhitespace, JoinLines
}

internal static class EditorTextTransformations
{
    internal static IReadOnlyDictionary<TextTransformation, RoutedCommand> Commands { get; } =
        Enum.GetValues<TextTransformation>().ToDictionary(operation => operation, _ => new RoutedCommand());

    internal static void Attach(TextBox editor)
    {
        foreach (var (operation, command) in Commands)
            editor.CommandBindings.Add(new CommandBinding(command,
                (_, e) => { e.Handled = true; Apply(editor, operation); },
                (_, e) =>
                {
                    e.CanExecute = editor.IsEnabled && !editor.IsReadOnly && editor.IsVisible && editor.SelectionLength > 0;
                    e.Handled = true;
                }));
        AddMenuItems(editor.ContextMenu, () => editor);
    }

    internal static void AddMenuItems(ItemsControl menu, Func<TextBox> getEditor)
    {
        menu.Items.Add(new Separator());
        EditorTextWrapping.AddMenuItem(menu, getEditor);
        var group = new MenuItem { Header = "Transform Text" };
        var style = menu is ContextMenu ? getEditor().TryFindResource("NotedContextMenuItem") as System.Windows.Style : null;
        if (style is not null) group.Style = style;
        foreach (var (label, operation) in new[]
        {
            ("Uppercase", TextTransformation.Uppercase), ("Lowercase", TextTransformation.Lowercase),
            ("Toggle Case", TextTransformation.ToggleCase), ("Sort Lines Ascending", TextTransformation.SortAscending),
            ("Sort Lines Descending", TextTransformation.SortDescending), ("Remove Duplicate Lines", TextTransformation.RemoveDuplicates),
            ("Trim Trailing Whitespace", TextTransformation.TrimTrailingWhitespace), ("Join Lines", TextTransformation.JoinLines)
        })
        {
            var item = new MenuItem { Header = label, Command = Commands[operation], CommandTarget = getEditor() };
            if (style is not null) item.Style = style;
            group.Items.Add(item);
        }
        menu.Items.Add(group);
        void Refresh()
        {
            foreach (var item in group.Items.OfType<MenuItem>()) item.CommandTarget = getEditor();
        }
        if (menu is ContextMenu context) context.Opened += (_, _) => Refresh();
        else if (menu is MenuItem parent) parent.SubmenuOpened += (_, _) => Refresh();
        group.SubmenuOpened += (_, _) => Refresh();
    }

    internal static bool Apply(TextBox editor, TextTransformation operation)
    {
        if (!editor.IsEnabled || editor.IsReadOnly || editor.SelectionLength == 0) return false;
        var text = editor.Text;
        var start = editor.SelectionStart;
        var end = start + editor.SelectionLength;
        var selectedSeparator = 0;
        string replacement;
        if (operation is TextTransformation.Uppercase or TextTransformation.Lowercase or TextTransformation.ToggleCase)
        {
            var selected = text[start..end];
            replacement = operation switch
            {
                TextTransformation.Uppercase => selected.ToUpperInvariant(),
                TextTransformation.Lowercase => selected.ToLowerInvariant(),
                _ => ToggleCase(selected)
            };
        }
        else
        {
            var lines = new TextLineMap(text);
            var (first, last) = lines.SelectedLines(start, end);
            if (end == lines.End(last)) selectedSeparator = lines.End(last) - lines.ContentEnd(last);
            start = lines.Starts[first];
            end = lines.ContentEnd(last);
            var values = Enumerable.Range(first, last - first + 1)
                .Select(line => text[lines.Starts[line]..lines.ContentEnd(line)]).ToArray();
            IEnumerable<string> transformed = operation switch
            {
                TextTransformation.SortAscending => values.Order(StringComparer.Ordinal),
                TextTransformation.SortDescending => values.OrderDescending(StringComparer.Ordinal),
                TextTransformation.RemoveDuplicates => values.Distinct(StringComparer.Ordinal),
                TextTransformation.TrimTrailingWhitespace => values.Select(value => value.TrimEnd(' ', '\t')),
                TextTransformation.JoinLines => [],
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            if (operation == TextTransformation.JoinLines)
            {
                if (values.Length == 1) return true;
                replacement = string.Join(" ", values.Select((value, index) => index == 0 ? value.TrimEnd(' ', '\t')
                    : index == values.Length - 1 ? value.TrimStart(' ', '\t') : value.Trim(' ', '\t'))
                    .Where(value => value.Length > 0));
            }
            else
            {
                var builder = new StringBuilder();
                var index = first;
                foreach (var value in transformed)
                {
                    if (index > first) builder.Append(text[lines.ContentEnd(index - 1)..lines.Starts[index]]);
                    builder.Append(value);
                    index++;
                }
                replacement = builder.ToString();
            }
        }
        if (text.AsSpan(start, end - start).SequenceEqual(replacement)) return true;
        var vertical = editor.VerticalOffset;
        var horizontal = editor.HorizontalOffset;
        editor.BeginChange();
        try
        {
            editor.Select(start, end - start);
            editor.SelectedText = replacement;
            editor.Select(start, replacement.Length + selectedSeparator);
        }
        finally { editor.EndChange(); }
        editor.ScrollToVerticalOffset(vertical);
        editor.ScrollToHorizontalOffset(horizontal);
        return true;
    }

    private static string ToggleCase(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
            result.Append((Rune.IsUpper(rune) ? Rune.ToLowerInvariant(rune)
                : Rune.IsLower(rune) ? Rune.ToUpperInvariant(rune) : rune).ToString());
        return result.ToString();
    }
}
