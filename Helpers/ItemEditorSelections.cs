using System.Windows.Controls;
using System.Security.Cryptography;
using System.Text;
using Noted.Models;

namespace Noted.Helpers;

internal sealed class ItemEditorSelections<TItem> where TItem : class
{
    private readonly Dictionary<(TItem Item, string Field), UtilityEditorPosition> _selections = [];

    internal void Remember(TItem item, TextBox editor) =>
        _selections[(item, editor.Name)] = new(Hash(editor.Text), editor.CaretIndex,
            editor.SelectionStart, editor.SelectionLength, editor.HorizontalOffset, editor.VerticalOffset);

    internal UtilityEditorPosition? Get(TItem item, string field) => _selections.GetValueOrDefault((item, field));

    internal void Load(TItem item, string field, UtilityEditorPosition? position)
    {
        if (position is not null) _selections[(item, field)] = position;
    }

    internal bool Restore(TItem item, TextBox editor)
    {
        if (!_selections.TryGetValue((item, editor.Name), out var position) ||
            position.TextHash != Hash(editor.Text)) return false;
        var start = position.SelectionLength == 0 ? position.CaretIndex : position.SelectionStart;
        if ((editor.SelectionStart, editor.SelectionLength) != (start, position.SelectionLength))
            Apply(editor, start, position.SelectionLength);
        if (editor.HorizontalOffset != position.HorizontalOffset)
            editor.ScrollToHorizontalOffset(position.HorizontalOffset);
        if (editor.VerticalOffset != position.VerticalOffset)
            editor.ScrollToVerticalOffset(position.VerticalOffset);
        return true;
    }

    internal static UtilityItemText CaptureText(string primary, string details) => new(Hash(primary), Hash(details));

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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
