using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using MyNotes.Models;
using MyNotes.Services;
using MyNotes.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace MyNotes;

public partial class MainWindow : Window {
    private readonly AppSettingsService _settingsService;
    private readonly NoteFileService _fileService;
    private readonly NotepadProcessService _notepadService;
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _renameBannerTimer;
    private bool _isUpdatingSettingsView;

    // Drag state
    private Point? _dragStart;
    private FrameworkElement? _dragElement;
    private bool _isDragging;
    private double _dragInitialLeft;
    private double _dragInitialTop;
    private const double _dragThreshold = 5.0;

    public MainWindow() {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        _fileService = new NoteFileService(_settingsService);
        _notepadService = new NotepadProcessService();
        _viewModel = new MainWindowViewModel(_fileService);
        _renameBannerTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(10)
        };
        _renameBannerTimer.Tick += RenameBannerTimer_Tick;

        OverlayButton.PreviewMouseLeftButtonDown += OverlayButton_MouseLeftButtonDown;
        OverlayButton.PreviewMouseMove += OverlayButton_MouseMove;
        OverlayButton.PreviewMouseLeftButtonUp += OverlayButton_MouseLeftButtonUp;
        NotesPanel.PreviewMouseLeftButtonDown += NotesPanel_PreviewMouseLeftButtonDown;
        NotesPanel.PreviewMouseMove += NotesPanel_PreviewMouseMove;
        NotesPanel.PreviewMouseLeftButtonUp += NotesPanel_PreviewMouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown += MainCanvas_MouseLeftButtonDown;
        KeyDown += MainWindow_KeyDown;

        FileList.ItemsSource = _viewModel.Notes;
        _viewModel.NotesLoaded += OnNotesLoaded;
        _viewModel.LoadNotes();
        UpdateNotesDirectoryDisplay();
        UpdateSettingsView();
        SetSettingsViewVisible(false);

        Closing += OnClosing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        // Span the overlay across the full virtual desktop so elements can be dragged to any monitor.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    // Sync HeaderText and restore the tracked selection after every reload of notes. 
    // This ensures the UI updates immediately after changes instead of waiting for the next FileSystemWatcher event.
    private void OnNotesLoaded(object? sender, EventArgs e) {
        UpdateHeaderText();
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

    private void SetSettingsViewVisible(bool isVisible) {
        NotesListView.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Content = isVisible ? "Notes" : "Settings";
        UpdateHeaderText();
        UpdateNotesDirectoryDisplay();
    }

    private void UpdateSettingsView() {
        _isUpdatingSettingsView = true;
        try {
            PromptForNoteNameOption.IsChecked = _settingsService.LoadPromptForNoteName();
            var timestampPlacement = _settingsService.LoadTimestampPlacement();
            TimestampNoneOption.IsChecked = timestampPlacement == NoteTimestampPlacement.None;
            TimestampTopOption.IsChecked = timestampPlacement == NoteTimestampPlacement.Top;
            TimestampBottomOption.IsChecked = timestampPlacement == NoteTimestampPlacement.Bottom;
            SettingsNotesDirectoryText.Text = _fileService.NotesDirectory;
        } finally {
            _isUpdatingSettingsView = false;
        }
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e) {
        var showSettings = SettingsView.Visibility != Visibility.Visible;
        if (showSettings)
            UpdateSettingsView();

        SetSettingsViewVisible(showSettings);
    }

    private void BackToNotesButton_Click(object sender, RoutedEventArgs e) {
        SetSettingsViewVisible(false);
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e) {
        try {
            var filename = CreateNewNoteFromCurrentSettings();
            if (filename is null)
                return;

            _viewModel.LoadNotes(filename);
            // OnNotesLoaded fires synchronously above, so selection is already set.
            _notepadService.Open(Path.Combine(_fileService.NotesDirectory, filename));
        } catch (Exception ex) {
            MessageBox.Show($"Failed to create note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? CreateNewNoteFromCurrentSettings() {
        if (!_settingsService.LoadPromptForNoteName())
            return _fileService.CreateNote();

        var attemptedName = string.Empty;
        while (true) {
            var dialog = new NewNoteNameDialog(attemptedName) {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return null;

            try {
                return _fileService.CreateNote(dialog.NoteName);
            } catch (InvalidOperationException ex) {
                attemptedName = dialog.NoteName;
                MessageBox.Show(ex.Message,
                    "New Note Name", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void PromptForNoteNameOption_Changed(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView)
            return;

        _settingsService.SavePromptForNoteName(PromptForNoteNameOption.IsChecked == true);
    }

    private void TimestampPlacementOption_Checked(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView)
            return;

        var timestampPlacement = sender switch {
            RadioButton { Name: nameof(TimestampTopOption) } => NoteTimestampPlacement.Top,
            RadioButton { Name: nameof(TimestampBottomOption) } => NoteTimestampPlacement.Bottom,
            _ => NoteTimestampPlacement.None
        };

        _settingsService.SaveTimestampPlacement(timestampPlacement);
    }

    private void DeleteNoteButton_Click(object sender, RoutedEventArgs e) {
        NoteItem? note = null;

        if (sender is FrameworkElement element && element.DataContext is NoteItem itemNote) {
            note = itemNote;
            FileList.SelectedItem = itemNote;
        } else {
            note = FileList.SelectedItem as NoteItem;
        }

        if (note is null) return;

        try {
            _fileService.DeleteNote(note.FileName);
            // FileSystemWatcher triggers a debounced reload automatically.
        } catch (Exception ex) {
            MessageBox.Show($"Failed to delete note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note)
            _viewModel.SelectedFileName = note.FileName;
    }

    private void OpenSelectedNote() {
        if (FileList.SelectedItem is not NoteItem note) return;

        var filePath = Path.Combine(_fileService.NotesDirectory, note.FileName);
        _viewModel.SelectedFileName = note.FileName;

        if (_notepadService.Open(filePath)) {
            _notepadService.Restore();
            return;
        }

        if (_notepadService.IsRunning)
            _notepadService.Restore();
    }

    private void OpenSelectedNote_Click(object sender, RoutedEventArgs e) {
        OpenSelectedNote();
    }

    private void ChangeFolderButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFolderDialog {
            Title = "Choose Notes Folder",
            InitialDirectory = _fileService.NotesDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        try {
            if (!EnsureCurrentNoteCanClose("finish with the currently open note before changing folders"))
                return;

            var changed = _fileService.ChangeNotesDirectory(dialog.FolderName);
            _viewModel.SelectedFileName = null;
            if (changed)
                _viewModel.LoadNotes();

            UpdateNotesDirectoryDisplay();
            UpdateSettingsView();
        } catch (Exception ex) {
            MessageBox.Show($"Failed to change notes folder:\n{ex.Message}",
                "Folder Change Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (sender is FrameworkElement element && element.DataContext is NoteItem itemNote) {
            FileList.SelectedItem = itemNote;
            StartRenaming(itemNote);
            return;
        }

        if (FileList.SelectedItem is NoteItem selectedNote)
            StartRenaming(selectedNote);
    }

    private void ListBoxItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (IsWithinNoteRowActionControl(e.OriginalSource as DependencyObject))
            return;

        if (sender is ListBoxItem item && item.DataContext is NoteItem note) {
            FileList.SelectedItem = note;
            OpenSelectedNote();
            e.Handled = true;
        }
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter && FileList.SelectedItem is NoteItem) {
            OpenSelectedNote();
            e.Handled = true;
        } else if (e.Key == Key.F2 && FileList.SelectedItem is NoteItem note) {
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
        var oldFileName = note.FileName;
        var oldFilePath = Path.Combine(_fileService.NotesDirectory, oldFileName);
        var wasNoteOpen = _notepadService.IsFileOpen(oldFilePath);
        var (success, newFileName, error) = _fileService.RenameNote(note.FileName, newDisplayName);
        if (!success) {
            if (error is not null)
                MessageBox.Show(error, "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _viewModel.LoadNotes(newFileName ?? note.FileName);
        if (!wasNoteOpen || newFileName is null)
            return;

        ShowRenameNotice(_viewModel.FindNote(newFileName), oldFileName);
        if (_notepadService.Open(Path.Combine(_fileService.NotesDirectory, newFileName))) {
            // _notepadService.Restore();
        }
    }

    private void ShowRenameNotice(NoteItem? renamedNote, string oldFileName) {
        RenameNoticeText.Text =
            $"For any unsaved changes, look for the unclosed tab with the old file name: {oldFileName}.";
        _renameBannerTimer.Stop();

        Dispatcher.InvokeAsync(() => {
            FileList.UpdateLayout();

            var targetElement = renamedNote is not null
                ? FileList.ItemContainerGenerator.ContainerFromItem(renamedNote) as FrameworkElement
                : null;

            PositionRenameNotice(targetElement ?? NotesPanel);
            RenameNoticePopup.IsOpen = true;
            _renameBannerTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    private void RenameBannerTimer_Tick(object? sender, EventArgs e) {
        _renameBannerTimer.Stop();
        RenameNoticePopup.IsOpen = false;
    }

    private void PositionRenameNotice(FrameworkElement targetElement) {
        RenameNoticePopup.PlacementTarget = targetElement;

        if (ReferenceEquals(targetElement, NotesPanel)) {
            RenameNoticePopup.HorizontalOffset = Math.Max(16, (NotesPanel.ActualWidth - 220) / 2);
            RenameNoticePopup.VerticalOffset = 68;
            return;
        }

        var targetWidth = Math.Max(targetElement.ActualWidth, 220);
        var targetHeight = Math.Max(targetElement.ActualHeight, 36);
        RenameNoticePopup.HorizontalOffset = Math.Max(8, targetWidth - 228);
        RenameNoticePopup.VerticalOffset = Math.Max(0, (targetHeight - 52) / 2);
    }

    private bool EnsureCurrentNoteCanClose(string actionDescription) {
        if (_notepadService.TryCloseCurrentNote())
            return true;

        MessageBox.Show(
            $"Please save, close, or finish with the open note before trying to {actionDescription}.",
            "Note Still Open",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
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
        BeginDrag(OverlayButton, e);
    }

    private void OverlayButton_MouseMove(object sender, MouseEventArgs e) {
        UpdateDrag(OverlayButton, e);
    }

    private void OverlayButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        var wasDragging = _isDragging;
        EndDrag(OverlayButton, e);

        if (!wasDragging && e.ChangedButton == MouseButton.Left) {
            OverlayButton_Click(OverlayButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void NotesPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (!CanStartNotesPanelDrag(e.OriginalSource as DependencyObject))
            return;

        BeginDrag(NotesPanel, e);
    }

    private void NotesPanel_PreviewMouseMove(object sender, MouseEventArgs e) {
        UpdateDrag(NotesPanel, e);
    }

    private void NotesPanel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        EndDrag(NotesPanel, e);
    }

    private void BeginDrag(FrameworkElement element, MouseButtonEventArgs e) {
        if (e.ChangedButton != MouseButton.Left)
            return;

        _dragElement = element;
        _isDragging = false;
        _dragStart = e.GetPosition(MainCanvas);
        _dragInitialLeft = GetCanvasLeft(element);
        _dragInitialTop = GetCanvasTop(element);
    }

    private void UpdateDrag(FrameworkElement element, MouseEventArgs e) {
        if (_dragElement != element || !_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = e.GetPosition(MainCanvas) - _dragStart.Value;
        if (delta.Length <= _dragThreshold)
            return;

        if (!_isDragging) {
            _isDragging = true;
            element.CaptureMouse();
        }

        Canvas.SetLeft(element, _dragInitialLeft + delta.X);
        Canvas.SetTop(element, _dragInitialTop + delta.Y);
        Canvas.SetRight(element, double.NaN);
        Canvas.SetBottom(element, double.NaN);
        e.Handled = true;
    }

    private void EndDrag(FrameworkElement element, MouseButtonEventArgs e) {
        if (_dragElement != element)
            return;

        _dragElement = null;
        _dragStart = null;
        if (element.IsMouseCaptured)
            element.ReleaseMouseCapture();

        if (_isDragging) {
            _isDragging = false;
            e.Handled = true;
        }
    }

    private static bool CanStartNotesPanelDrag(DependencyObject? source) {
        return !IsWithinInteractiveControl(source);
    }

    private static bool IsWithinNoteRowActionControl(DependencyObject? source) {
        var current = source;
        while (current is not null) {
            if (current is ButtonBase or TextBox or ScrollBar or Thumb)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static bool IsWithinInteractiveControl(DependencyObject? source) {
        var current = source;
        while (current is not null) {
            if (current is ButtonBase or TextBox or ListBox or ListBoxItem or ScrollBar or Thumb)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child) {
        return child switch {
            Visual or Visual3D => VisualTreeHelper.GetParent(child),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => null
        };
    }

    private double GetCanvasLeft(FrameworkElement element) {
        var left = Canvas.GetLeft(element);
        if (!double.IsNaN(left))
            return left;

        var right = Canvas.GetRight(element);
        return right > 0 ? MainCanvas.ActualWidth - right - element.ActualWidth : 0;
    }

    private double GetCanvasTop(FrameworkElement element) {
        var top = Canvas.GetTop(element);
        if (!double.IsNaN(top))
            return top;

        var bottom = Canvas.GetBottom(element);
        return bottom > 0 ? MainCanvas.ActualHeight - bottom - element.ActualHeight : 0;
    }

    private void NotesPanelResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) {
        var currentLeft = GetCanvasLeft(NotesPanel);
        var currentTop = GetCanvasTop(NotesPanel);
        var maxWidth = Math.Max(NotesPanel.MinWidth, MainCanvas.ActualWidth - currentLeft - 8);
        var maxHeight = Math.Max(NotesPanel.MinHeight, MainCanvas.ActualHeight - currentTop - 8);

        NotesPanel.Width = Math.Clamp(NotesPanel.Width + e.HorizontalChange, NotesPanel.MinWidth, maxWidth);
        NotesPanel.Height = Math.Clamp(NotesPanel.Height + e.VerticalChange, NotesPanel.MinHeight, maxHeight);
        e.Handled = true;
    }
    #endregion

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) {
        _renameBannerTimer.Tick -= RenameBannerTimer_Tick;
        _renameBannerTimer.Stop();
        RenameNoticePopup.IsOpen = false;
        OverlayButton.PreviewMouseLeftButtonDown -= OverlayButton_MouseLeftButtonDown;
        OverlayButton.PreviewMouseMove -= OverlayButton_MouseMove;
        OverlayButton.PreviewMouseLeftButtonUp -= OverlayButton_MouseLeftButtonUp;
        NotesPanel.PreviewMouseLeftButtonDown -= NotesPanel_PreviewMouseLeftButtonDown;
        NotesPanel.PreviewMouseMove -= NotesPanel_PreviewMouseMove;
        NotesPanel.PreviewMouseLeftButtonUp -= NotesPanel_PreviewMouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown -= MainCanvas_MouseLeftButtonDown;
        KeyDown -= MainWindow_KeyDown;

        _viewModel.Dispose();
        _notepadService.Dispose();
        _fileService.Dispose();
    }
}
