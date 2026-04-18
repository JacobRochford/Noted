using System.Windows;
using System.Windows.Input;

namespace MyNotes;

public partial class NewNoteNameDialog : Window {
    public string NoteName => NoteNameTextBox.Text.Trim();

    public NewNoteNameDialog(string initialName = "") {
        InitializeComponent();
        NoteNameTextBox.Text = initialName;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        NoteNameTextBox.Focus();
        NoteNameTextBox.SelectAll();
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
    }

    private void NoteNameTextBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            DialogResult = true;
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            DialogResult = false;
            e.Handled = true;
        }
    }
}