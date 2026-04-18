using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyNotes.Models;
using MyNotes.Services;
using MyNotes.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace MyNotes;

public partial class MainWindow : Window {
    private readonly NoteFileService _fileService;
    private readonly NotepadProcessService _notepadService;
    private readonly MainWindowViewModel _viewModel;

    // Drag state
    private Point? _dragStart;
    private bool _isDragging;
    private double _buttonInitialLeft;
    private double _buttonInitialTop;
    private const double _dragThreshold = 5.0;

    public MainWindow() {
        InitializeComponent();

        _fileService = new NoteFileService();
        _notepadService = new NotepadProcessService();
        _viewModel = new MainWindowViewModel(_fileService);

        OverlayButton.MouseLeftButtonDown += OverlayButton_MouseLeftButtonDown;
        OverlayButton.MouseMove += OverlayButton_MouseMove;
        OverlayButton.MouseLeftButtonUp += OverlayButton_MouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown += MainCanvas_MouseLeftButtonDown;
        KeyDown += MainWindow_KeyDown;

        FileList.ItemsSource = _viewModel.Notes;
        _viewModel.NotesLoaded += OnNotesLoaded;
        _viewModel.LoadNotes();
        UpdateNotesDirectoryDisplay();

        Closing += OnClosing;
    }

    // Sync HeaderText and restore the tracked selection after every reload of notes. 
    // This ensures the UI updates immediately after changes instead of waiting for the next FileSystemWatcher event.
    private void OnNotesLoaded(object? sender, EventArgs e) {
        UpdateNotesDirectoryDisplay();
        if (_viewModel.SelectedFileName is not null)
            FileList.SelectedItem = _viewModel.FindNote(_viewModel.SelectedFileName);
    }

    private void UpdateNotesDirectoryDisplay() {
        SettingsNotesDirectoryText.Text = _fileService.NotesDirectory;
        SettingsButton.ToolTip = SettingsView.Visibility == Visibility.Visible
            ? "Return to notes"
            : "Open settings";
    }

    private void UpdateHeaderText() {
        HeaderText.Text = SettingsView.Visibility == Visibility.Visible
            ? "Settings"
            : _viewModel.HeaderText;
    }
    private void HideNotesPanelAndMinimizeNotepad() {
        NotesPanel.Visibility = Visibility.Collapsed;
        _notepadService.Minimize();
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e) {
        if (NotesPanel.Visibility == Visibility.Visible) {
            HideNotesPanelAndMinimizeNotepad();
        } else {
            NotesPanel.Visibility = Visibility.Visible;
            if (_notepadService.IsRunning)
                _notepadService.Restore();
        }
    }

    private void MinimizeNotesButton_Click(object sender, RoutedEventArgs e) {
        HideNotesPanelAndMinimizeNotepad();
    }
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e) {
        try {
            var filename = _fileService.CreateNote();
            _viewModel.LoadNotes(filename);
            // OnNotesLoaded fires synchronously above, so selection is already set.
            _notepadService.Open(Path.Combine(_fileService.NotesDirectory, filename));
        } catch (Exception ex) {
            MessageBox.Show($"Failed to create note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteNoteButton_Click(object sender, RoutedEventArgs e) {
        if (FileList.SelectedItem is not NoteItem note) return;

        var result = MessageBox.Show(
            $"Delete \"{note.DisplayName}\"?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        if (_notepadService.IsRunning)
            _notepadService.Close();

        try {
            _fileService.DeleteNote(note.FileName);
            // FileSystemWatcher triggers a debounced reload automatically.
        } catch (Exception ex) {
            MessageBox.Show($"Failed to delete note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note) {
            _viewModel.SelectedFileName = note.FileName;
            _notepadService.Open(Path.Combine(_fileService.NotesDirectory, note.FileName));
        }
    }

    private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (NotesPanel.Visibility == Visibility.Visible &&
            !NotesPanel.IsMouseOver && !OverlayButton.IsMouseOver) {
            HideNotesPanelAndMinimizeNotepad();
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && NotesPanel.Visibility == Visibility.Visible) {
            HideNotesPanelAndMinimizeNotepad();
            e.Handled = true;
        }
    }

    #region Note Renaming
    private void RenameNote_Click(object sender, RoutedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note)
            StartRenaming(note);
    }

    private void ListBoxItem_DoubleClick(object sender, MouseButtonEventArgs e) {
        if (sender is ListBoxItem item && item.DataContext is NoteItem note) {
            StartRenaming(note);
            e.Handled = true;
        }
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F2 && FileList.SelectedItem is NoteItem note) {
            StartRenaming(note);
            e.Handled = true;
        }
    }

    private void StartRenaming(NoteItem note) {
        note.EditableName = Path.GetFileNameWithoutExtension(note.FileName);
        note.IsEditing = true;
        // Focus the textbox after the UI updates
        Dispatcher.InvokeAsync(() => {
            var listBoxItem = FileList.ItemContainerGenerator.ContainerFromItem(note) as ListBoxItem;
            if (listBoxItem != null) {
                var textBox = FindVisualChild<TextBox>(listBoxItem);
                textBox?.Focus();
                textBox?.SelectAll();
            }
        });
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e) {
        var textBox = sender as TextBox;
        var note = FileList.SelectedItem as NoteItem;

        if (note == null) return;
        var originalName = Path.GetFileNameWithoutExtension(note.FileName);

        if (e.Key == Key.Return) {
            var newName = textBox?.Text.Trim() ?? note.EditableName;
            if (!string.IsNullOrWhiteSpace(newName) && newName != originalName)
                CommitRename(note, newName);
            note.IsEditing = false;
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            note.EditableName = originalName;
            note.IsEditing = false;
            e.Handled = true;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) {
        var textBox = sender as TextBox;
        if (textBox == null) return;

        var listBoxItem = FindVisualParent<ListBoxItem>(textBox);
        if (listBoxItem?.DataContext is not NoteItem note) return;
        if (!note.IsEditing) return;
        var originalName = Path.GetFileNameWithoutExtension(note.FileName);

        var newName = textBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(newName) && newName != originalName)
            CommitRename(note, newName);
        else
            note.EditableName = originalName;

        note.IsEditing = false;
    }

    private void CommitRename(NoteItem note, string newDisplayName) {
        var (success, newFileName, error) = _fileService.RenameNote(note.FileName, newDisplayName);
        if (!success) {
            if (error is not null)
                MessageBox.Show(error, "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _viewModel.LoadNotes(newFileName ?? note.FileName);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
        if (parent == null) return null;

        int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++) {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;

            var foundChild = FindVisualChild<T>(child);
            if (foundChild != null) return foundChild;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;

        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
    }

    #endregion

    #region Overlay Button Drag
    private void OverlayButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragStart = e.GetPosition(MainCanvas);
        _buttonInitialLeft = Canvas.GetLeft(OverlayButton);
        _buttonInitialTop = Canvas.GetTop(OverlayButton);

        if (double.IsNaN(_buttonInitialLeft))
            _buttonInitialLeft = Canvas.GetRight(OverlayButton) > 0
                ? MainCanvas.ActualWidth - Canvas.GetRight(OverlayButton) - OverlayButton.ActualWidth
                : 0;
        if (double.IsNaN(_buttonInitialTop))
            _buttonInitialTop = Canvas.GetBottom(OverlayButton) > 0
                ? MainCanvas.ActualHeight - Canvas.GetBottom(OverlayButton) - OverlayButton.ActualHeight
                : 0;
    }

    private void OverlayButton_MouseMove(object sender, MouseEventArgs e) {
        if (!_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = e.GetPosition(MainCanvas) - _dragStart.Value;
        if (delta.Length <= _dragThreshold) return;

        if (!_isDragging) {
            _isDragging = true;
            OverlayButton.CaptureMouse();
        }

        Canvas.SetLeft(OverlayButton, _buttonInitialLeft + delta.X);
        Canvas.SetTop(OverlayButton, _buttonInitialTop + delta.Y);
        Canvas.SetRight(OverlayButton, double.NaN);
        Canvas.SetBottom(OverlayButton, double.NaN);
        e.Handled = true;
    }

    private void OverlayButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (!_dragStart.HasValue) return;
        _dragStart = null;

        if (_isDragging) {
            _isDragging = false;
            OverlayButton.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
    #endregion

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) {
        OverlayButton.MouseLeftButtonDown -= OverlayButton_MouseLeftButtonDown;
        OverlayButton.MouseMove -= OverlayButton_MouseMove;
        OverlayButton.MouseLeftButtonUp -= OverlayButton_MouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown -= MainCanvas_MouseLeftButtonDown;
        KeyDown -= MainWindow_KeyDown;

        _viewModel.Dispose();
        _notepadService.Dispose();
        _fileService.Dispose();
    }
}
