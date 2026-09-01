using System.Windows;
using System.Windows.Input;

namespace Noted;

public partial class NewNoteNameDialog : Window {
    public string NoteName => NoteNameTextBox.Text.Trim();

    public NewNoteNameDialog(string initialName = "")
        : this(initialName, "Create Note", "Enter note title", "Create") {
    }

    private NewNoteNameDialog(
        string initialName,
        string title,
        string prompt,
        string confirmText) {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ConfirmButton.Content = confirmText;
        NoteNameTextBox.Text = initialName;
        Loaded += OnLoaded;
    }

    public static NewNoteNameDialog ForFirstSave(string initialName = "") {
        return new NewNoteNameDialog(
            initialName,
            "Name Note",
            "Name this note before saving",
            "Save");
    }

    public static NewNoteNameDialog ForRename(string initialName) {
        return new NewNoteNameDialog(
            initialName,
            "Rename Note",
            "Enter a new name",
            "Rename");
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        NoteNameTextBox.Focus();
        NoteNameTextBox.SelectAll();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) {
        TryConfirm();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
    }

    private void NoteNameTextBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            TryConfirm();
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void NoteNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
        if (ConfirmButton is not null)
            ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteNameTextBox.Text);
    }

    private void TryConfirm() {
        if (string.IsNullOrWhiteSpace(NoteNameTextBox.Text))
            return;

        DialogResult = true;
    }
}
