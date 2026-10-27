using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Noted;

internal sealed record QuickOpenNote(string FullPath, string DisplayPath)
{
    public string FileName => Path.GetFileName(FullPath);
}

internal partial class QuickOpenDialog : Window
{
    private readonly ObservableCollection<QuickOpenNote> _notes;
    private readonly ICollectionView _results;
    internal string? SelectedFilePath { get; private set; }

    internal QuickOpenDialog(IEnumerable<QuickOpenNote> notes)
    {
        InitializeComponent();
        _notes = new(notes);
        _results = CollectionViewSource.GetDefaultView(_notes);
        _results.Filter = item => item is QuickOpenNote note &&
            note.FileName.Contains(QueryTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        ResultsList.ItemsSource = _results;
        RefreshResults();
        Loaded += (_, _) => QueryTextBox.Focus();
    }

    private void QueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_results is not null) RefreshResults();
    }

    private void RefreshResults()
    {
        _results.Refresh();
        ResultsList.SelectedIndex = ResultsList.Items.Count > 0 ? 0 : -1;
        EmptyText.Visibility = ResultsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = "";
    }

    internal bool TrySelectCurrent()
    {
        if (ResultsList.SelectedItem is not QuickOpenNote note) return false;
        if (!File.Exists(note.FullPath))
        {
            _notes.Remove(note);
            RefreshResults();
            ErrorText.Text = "This note was moved or deleted. Reopen Quick Open to refresh the note list.";
            return false;
        }
        SelectedFilePath = note.FullPath;
        return true;
    }

    private void Confirm()
    {
        if (TrySelectCurrent()) DialogResult = true;
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) is ListBoxItem) Confirm();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter && (QueryTextBox.IsKeyboardFocusWithin || ResultsList.IsKeyboardFocusWithin))
        {
            Confirm();
            e.Handled = true;
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down) &&
            (QueryTextBox.IsKeyboardFocusWithin || ResultsList.IsKeyboardFocusWithin))
        {
            if (ResultsList.Items.Count > 0)
            {
                ResultsList.SelectedIndex = Math.Clamp(ResultsList.SelectedIndex + (e.Key == Key.Up ? -1 : 1), 0, ResultsList.Items.Count - 1);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
    }
}
