using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MessageBox = System.Windows.Forms.MessageBox;

namespace MyNotes;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly string NotesDirectory;
    private System.Windows.Point? _dragStart = null;
    private const double DragThreshold = 5.0;
    private string _activeNotePath = null;
    private Process _notepadProcess = null;
    private Process _lastOpenedNotepad = null;
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private double _buttonInitialLeft;
    private double _buttonInitialTop;

    public MainWindow() {
        InitializeComponent();

        OverlayButton.PreviewMouseLeftButtonDown += OverlayButton_PreviewMouseLeftButtonDown;
        OverlayButton.PreviewMouseMove += OverlayButton_PreviewMouseMove;
        OverlayButton.PreviewMouseLeftButtonUp += OverlayButton_PreviewMouseLeftButtonUp;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        NotesDirectory = Path.Combine(appDir, "Notes");
        Directory.CreateDirectory(NotesDirectory);

        LoadFileList();

        var mostRecentFile = Directory.GetFiles(NotesDirectory, "*.txt")
            .OrderByDescending(f => new FileInfo(f).LastWriteTime)
            .FirstOrDefault();

        if (mostRecentFile != null) {
            FileList.SelectedItem = Path.GetFileName(mostRecentFile);
        }

        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
        e.Cancel = true;
        Hide();
    }

    private void OverlayButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Left) {
            _dragStart = e.GetPosition(MainCanvas);
            _buttonInitialLeft = Canvas.GetLeft(OverlayButton);
            _buttonInitialTop = Canvas.GetTop(OverlayButton);

            if (double.IsNaN(_buttonInitialLeft) && Canvas.GetRight(OverlayButton) > 0) {
                _buttonInitialLeft = MainCanvas.ActualWidth - Canvas.GetRight(OverlayButton) - OverlayButton.ActualWidth;
            }
            if (double.IsNaN(_buttonInitialTop) && Canvas.GetBottom(OverlayButton) > 0) {
                _buttonInitialTop = MainCanvas.ActualHeight - Canvas.GetBottom(OverlayButton) - OverlayButton.ActualHeight;
            }

            if (double.IsNaN(_buttonInitialLeft)) _buttonInitialLeft = 0;
            if (double.IsNaN(_buttonInitialTop)) _buttonInitialTop = 0;

            OverlayButton.CaptureMouse();
            e.Handled = false;   // allow normal click if not dragging
        }
    }

    private void OverlayButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e) {
        if (!_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPos = e.GetPosition(MainCanvas);
        var delta = currentPos - _dragStart.Value;

        if (delta.Length > DragThreshold) {
            var newLeft = _buttonInitialLeft + delta.X;
            var newTop = _buttonInitialTop + delta.Y;

            Canvas.SetLeft(OverlayButton, newLeft);
            Canvas.SetTop(OverlayButton, newTop);
            Canvas.SetRight(OverlayButton, double.NaN);
            Canvas.SetBottom(OverlayButton, double.NaN);

            e.Handled = true;
        }
    }

    private void OverlayButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (_dragStart.HasValue) {
            var currentPos = e.GetPosition(MainCanvas);
            var delta = currentPos - _dragStart.Value;

            OverlayButton.ReleaseMouseCapture();
            _dragStart = null;

            if (delta.Length > DragThreshold) {
                e.Handled = true; // swallow click if dragged
            }
        }
    }

    // Single click: show/hide list & open/minimize Notepad
    private void OverlayButton_Click(object sender, RoutedEventArgs e) {
        if (FileList.Visibility == Visibility.Visible) {
            FileList.Visibility = Visibility.Collapsed;

            // TODO: minimize window
        } else {
            FileList.Visibility = Visibility.Visible;

        }
    }

    // Double click: New Notepad instance
    private void OverlayButton_DoubleClick(object sender, MouseButtonEventArgs e) {
        var now = DateTime.Now;
        var timestamp = now.ToString("yyyy-MM-dd HH:mm:ss");

        var filename = $"Note_{now:yyyy-MM-dd_HH-mm-ss}.txt";
        var fullPath = Path.Combine(NotesDirectory, filename);

        try {
            var content = $"Created: {timestamp}\r\n\r\n";
            File.WriteAllText(fullPath, content);

            LoadFileList();

            FileList.SelectedItem = filename;

            // Open new Notepad instance
            var proc = Process.Start("notepad.exe", $"\"{fullPath}\"");
            if (proc != null) {
                _lastOpenedNotepad = proc;
            }
        } catch (Exception ex) {
            MessageBox.Show($"Failed to create new note:\n{ex.Message}");
        }
    }

    // User picks file from list
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (FileList.SelectedItem is string fileName && !string.IsNullOrWhiteSpace(fileName)) {
            var fullPath = Path.Combine(NotesDirectory, fileName);

            try {
                var process = Process.Start("notepad.exe", $"\"{fullPath}\"");
                if (process != null) {
                    _lastOpenedNotepad = process;
                }
            } catch (Exception ex) {
                MessageBox.Show($"Failed to open note:\n{ex.Message}");
            }
        }
    }

    private void LoadFileList() {
        var files = Directory.GetFiles(NotesDirectory, "*.txt")
            .OrderByDescending(f => new FileInfo(f).LastWriteTime)
            .Select(Path.GetFileName)
            .ToList();

        FileList.ItemsSource = files;
    }

    public void Cleanup() {
        _notepadProcess = null;
    }
}