using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyNotes.Models;
using MessageBox = System.Windows.MessageBox;

namespace MyNotes;

public partial class MainWindow : Window {
    private readonly string NotesDirectory;
    private readonly ObservableCollection<NoteItem> _notes = new();
    private Process? _notepadProcess;
    private string? _notepadFilePath;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounceTimer;

    // Drag state
    private Point? _dragStart;
    private bool _isDragging;
    private double _buttonInitialLeft;
    private double _buttonInitialTop;
    private const double _dragThreshold = 5.0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    public MainWindow() {
        InitializeComponent();

        OverlayButton.MouseLeftButtonDown += OverlayButton_MouseLeftButtonDown;
        OverlayButton.MouseMove += OverlayButton_MouseMove;
        OverlayButton.MouseLeftButtonUp += OverlayButton_MouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown += MainCanvas_MouseLeftButtonDown;
        KeyDown += MainWindow_KeyDown;

        NotesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Notes");
        Directory.CreateDirectory(NotesDirectory);

        FileList.ItemsSource = _notes;
        LoadFileList();
        ListenForFileChanges();

        Closing += OnClosing;
    }

    private void ListenForFileChanges() {
        _watcher = new FileSystemWatcher(NotesDirectory, "*.txt") {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnNotesChanged;
        _watcher.Deleted += OnNotesChanged;
        _watcher.Renamed += (_, _) => Dispatcher.Invoke(DebounceRefresh);
    }

    private void OnNotesChanged(object sender, FileSystemEventArgs e) {
        Dispatcher.Invoke(DebounceRefresh);
    }

    private void DebounceRefresh() {
        // Reuse the timer if it exists
        if (_debounceTimer == null) {
            _debounceTimer = new DispatcherTimer {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _debounceTimer.Tick += (_, _) => {
                _debounceTimer.Stop();
                LoadFileList();
            };
        }
        
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e) {
        if (NotesPanel.Visibility == Visibility.Visible) {
            NotesPanel.Visibility = Visibility.Collapsed;
            MinimizeNotepad();
        } else {
            NotesPanel.Visibility = Visibility.Visible;
            if (IsNotepadRunning()) {
                RestoreNotepad();
            } else if (_notes.Count > 0) {
                FileList.SelectedItem = _notes[0];
                OpenNoteInNotepad(Path.Combine(NotesDirectory, _notes[0].FileName));
            }
        }
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e) {
        var now = DateTime.Now;
        var filename = $"Note_{now:yyyy-MM-dd_HH-mm-ss}.txt";
        var fullPath = Path.Combine(NotesDirectory, filename);

        try {
            File.WriteAllText(fullPath, $"Created: {now:yyyy-MM-dd HH:mm:ss}\r\n\r\n");
            // FileSystemWatcher will auto-refresh the list
            // Select and open the new note after a brief delay for the watcher
            Dispatcher.InvokeAsync(() => {
                var item = _notes.FirstOrDefault(n => n.FileName == filename);
                if (item is not null) {
                    FileList.SelectedItem = item;
                    OpenNoteInNotepad(fullPath);
                }
            }, DispatcherPriority.Background);
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

        var fullPath = Path.Combine(NotesDirectory, note.FileName);

        // Close notepad if it has this file open
        if (IsNotepadRunning()) {
            try { _notepadProcess!.CloseMainWindow(); } catch { }
            _notepadProcess = null;
        }

        try {
            File.Delete(fullPath);
            // FileSystemWatcher will auto-refresh the list
        } catch (Exception ex) {
            MessageBox.Show($"Failed to delete note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note)
            OpenNoteInNotepad(Path.Combine(NotesDirectory, note.FileName));
    }

    private bool IsNotepadRunning() =>
        _notepadProcess is not null && !_notepadProcess.HasExited;

    private void OpenNoteInNotepad(string filePath) {
        // Don't open the same file again if it's already open
        if (_notepadFilePath == filePath && IsNotepadRunning()) {
            return;
        }

        if (IsNotepadRunning()) {
            try { _notepadProcess!.CloseMainWindow(); } catch { }
            _notepadProcess = null;
        }

        try {
            var psi = new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") {
                UseShellExecute = false
            };
            var process = Process.Start(psi);
            if (process is not null) {
                _notepadProcess = process;
                _notepadFilePath = filePath;
                // Wait for input on background thread to avoid blocking UI
                Task.Run(() => {
                    try {
                        process.WaitForInputIdle();
                    } catch {
                        // Process may have exited or other issue - ignore
                    }
                });
            }
        } catch (Exception ex) {
            MessageBox.Show($"Failed to open note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadFileList() {
        var selected = (FileList.SelectedItem as NoteItem)?.FileName;

        _notes.Clear();
        foreach (var path in Directory.GetFiles(NotesDirectory, "*.txt")
                     .OrderByDescending(f => new FileInfo(f).LastWriteTime)) {
            var name = Path.GetFileName(path);
            _notes.Add(new NoteItem {
                FileName = name,
                DisplayName = FormatNoteName(name)
            });
        }

        if (selected is not null)
            FileList.SelectedItem = _notes.FirstOrDefault(n => n.FileName == selected);

        HeaderText.Text = _notes.Count > 0
            ? $"My Notes ({_notes.Count})"
            : "My Notes";
    }

    private static string FormatNoteName(string fileName) {
        // Note_2026-03-28_11-44-06.txt → Mar 28, 2026  11:44 AM
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("Note_") &&
            DateTime.TryParseExact(stem[5..], "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) {
            return dt.ToString("MMM dd, yyyy  h:mm tt");
        }
        return fileName;
    }

    private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        // Click on empty canvas area hides the panel
        if (NotesPanel.Visibility == Visibility.Visible &&
            !NotesPanel.IsMouseOver && !OverlayButton.IsMouseOver) {
            NotesPanel.Visibility = Visibility.Collapsed;
            MinimizeNotepad();
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && NotesPanel.Visibility == Visibility.Visible) {
            NotesPanel.Visibility = Visibility.Collapsed;
            MinimizeNotepad();
            e.Handled = true;
        }
    }
    private void MinimizeNotepad() {
        if (IsNotepadRunning()) {
            Task.Run(() => {
                try {
                    // Find notepad window by class name instead of using MainWindowHandle
                    IntPtr handle = IntPtr.Zero;
                    for (int i = 0; i < 20; i++) {
                        handle = FindWindow("Notepad", null);
                        if (handle != IntPtr.Zero) break;
                        Thread.Sleep(100);
                    }

                    if (handle != IntPtr.Zero) {
                        ShowWindow(handle, SW_HIDE);
                    }
                } catch {
                    // Ignore errors in window hiding
                }
            });
        }
    }

    private void RestoreNotepad() {
        if (IsNotepadRunning()) {
            try {
                var handle = FindWindow("Notepad", null);
                if (handle != IntPtr.Zero) {
                    ShowWindow(handle, SW_SHOW);
                    SetForegroundWindow(handle);
                }
            } catch {
                // Ignore errors in window restore
            }
        }
    }


    #region 
    // Note Renaming Methods, Helpers, & Event Handlers
    private void RenameNote_Click(object sender, RoutedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note) {
            StartRenaming(note);
        }
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

        if (e.Key == Key.Return) {
            // Commit the rename
            var newName = textBox?.Text.Trim() ?? note.DisplayName;
            if (!string.IsNullOrWhiteSpace(newName) && newName != note.DisplayName) {
                CommitRename(note, newName);
            }
            note.IsEditing = false;
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            // Cancel rename
            note.IsEditing = false;
            e.Handled = true;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) {
        var textBox = sender as TextBox;
        if (textBox == null) return;

        // Get the item that owns this textbox (walk up the visual tree)
        var listBoxItem = FindVisualParent<ListBoxItem>(textBox);
        if (listBoxItem?.DataContext is not NoteItem note) return;
        if (!note.IsEditing) return;

        // Always save the new name when clicking outside
        var newName = textBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(newName)) {
            CommitRename(note, newName);
        }

        // Make sure to turn off editing on the correct note
        note.IsEditing = false;
    }

    private void CommitRename(NoteItem note, string newDisplayName) {
        var oldFileName = note.FileName;
        var oldPath = Path.Combine(NotesDirectory, oldFileName);

        // Create new filename from display name
        var newFileName = Path.GetInvalidFileNameChars().Aggregate(
            newDisplayName,
            (current, c) => current.Replace(c.ToString(), "")
        ).Trim();

        if (string.IsNullOrWhiteSpace(newFileName)) return;
        if (!newFileName.EndsWith(".txt")) newFileName += ".txt";

        if (newFileName == oldFileName) return; // No change

        var newPath = Path.Combine(NotesDirectory, newFileName);

        try {
            if (File.Exists(oldPath) && !File.Exists(newPath)) {
                File.Move(oldPath, newPath);
                LoadFileList();
            } else if (File.Exists(newPath)) {
                MessageBox.Show("A file with that name already exists.",
                    "Name Taken", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        } catch (Exception ex) {
            MessageBox.Show($"Failed to rename note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
        if (parent == null) return null;

        int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++) {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) {
                return typedChild;
            }

            var foundChild = FindVisualChild<T>(child);
            if (foundChild != null) {
                return foundChild;
            }
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

    #region
    // Overlay Event Handlers
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
        // Unsubscribe from events
        OverlayButton.MouseLeftButtonDown -= OverlayButton_MouseLeftButtonDown;
        OverlayButton.MouseMove -= OverlayButton_MouseMove;
        OverlayButton.MouseLeftButtonUp -= OverlayButton_MouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown -= MainCanvas_MouseLeftButtonDown;
        KeyDown -= MainWindow_KeyDown;

        // Clean up resources
        _watcher?.Dispose();
        if (_debounceTimer != null) {
            _debounceTimer.Stop();
            _debounceTimer = null;
        }
        if (IsNotepadRunning()) {
            try { _notepadProcess!.CloseMainWindow(); } catch { }
            _notepadProcess?.Dispose();
        }
    }
}
