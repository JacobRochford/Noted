using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Noted.Helpers;

internal static class PlainTextPaste
{
    internal static RoutedCommand PasteWithoutNormalization { get; } = new();

    internal static void Attach(TextBox editor, Func<bool> normalizationEnabled)
    {
        var bypassNormalization = false;
        DataObject.AddPastingHandler(editor, (_, e) =>
        {
            if (e.IsDragDrop)
                return;

            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text)
            {
                e.CancelCommand();
                return;
            }

            // Replace only the incoming data, leaving insertion and undo to WPF.
            e.DataObject = new DataObject(DataFormats.UnicodeText,
                Normalize(text, normalizationEnabled(), bypassNormalization));
            e.FormatToApply = DataFormats.UnicodeText;
        });

        editor.InputBindings.Add(new KeyBinding(PasteWithoutNormalization,
            new KeyGesture(Key.V, ModifierKeys.Control | ModifierKeys.Alt)));
        editor.CommandBindings.Add(new CommandBinding(PasteWithoutNormalization, (_, e) =>
        {
            e.Handled = true;
            bypassNormalization = true;
            try { editor.Paste(); }
            finally { bypassNormalization = false; }
        }));
    }

    internal static string Normalize(string text, bool enabled, bool bypass = false)
    {
        if (!enabled || bypass)
            return text;

        var result = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case '\u2018':
                case '\u2019':
                    result.Append('\'');
                    break;
                case '\u201C':
                case '\u201D':
                    result.Append('"');
                    break;
                case '\u00A0':
                case '\u202F':
                case '\u2007':
                    result.Append(' ');
                    break;
                case '\u00AD':
                    break;
                default:
                    result.Append(character);
                    break;
            }
        }

        return result.ToString();
    }
}
