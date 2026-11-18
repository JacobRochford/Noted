using System.Windows.Controls;

namespace Noted.Helpers;

internal sealed class ItemEditorSelections<TItem> where TItem : class
{
    private readonly Dictionary<(TItem Item, string Field), (int Start, int Length)> _selections = [];

    internal void Remember(TItem item, TextBox editor) =>
        _selections[(item, editor.Name)] = (editor.SelectionStart, editor.SelectionLength);

    internal bool Restore(TItem item, TextBox editor)
    {
        if (!_selections.TryGetValue((item, editor.Name), out var selection)) return false;
        if ((editor.SelectionStart, editor.SelectionLength) != selection)
            Apply(editor, selection.Start, selection.Length);
        return true;
    }

    internal void Forget(TItem item)
    {
        foreach (var key in _selections.Keys.Where(key => ReferenceEquals(key.Item, item)).ToArray())
            _selections.Remove(key);
    }

    internal static void Apply(TextBox editor, int start, int length)
    {
        start = Math.Min(start, editor.Text.Length);
        editor.Select(start, Math.Min(length, editor.Text.Length - start));
    }
}
