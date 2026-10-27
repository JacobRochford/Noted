using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Noted.Helpers;

namespace Noted;

internal partial class GoToLineDialog : Window
{
    private readonly TextBoxBase _editor;

    internal GoToLineDialog(TextBoxBase editor, int currentLine, int lineCount)
    {
        InitializeComponent();
        _editor = editor;
        PromptText.Text = $"Line number (1 to {lineCount})";
        LineNumberTextBox.Text = currentLine.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => { LineNumberTextBox.Focus(); LineNumberTextBox.SelectAll(); };
    }

    private void GoButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (EditorNavigation.TryGoToLine(_editor, LineNumberTextBox.Text, out var error)) DialogResult = true;
        else
        {
            ErrorText.Text = error;
            LineNumberTextBox.Focus();
            LineNumberTextBox.SelectAll();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Enter && LineNumberTextBox.IsKeyboardFocusWithin) { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
