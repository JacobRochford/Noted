using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Noted.Helpers;
using Noted.ViewModels;

namespace Noted;

public partial class ChecklistUrgentReminderWindow : Window
{
    private readonly ChecklistUrgentRemindersViewModel _viewModel;
    private IntPtr _monitor;
    private long _inputStarted;
    private bool _compact;
    private bool _allowClose;
    private bool _closed;

    internal ChecklistUrgentReminderWindow(ChecklistUrgentRemindersViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }
    internal void ShowAlert()
    {
        if (_closed) return;
        if (IsVisible)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Reposition();
            }
            return;
        }
        if (_monitor == IntPtr.Zero) _monitor = WindowInterop.MonitorFromWindow(WindowInterop.GetForegroundWindow(), 2);
        new WindowInteropHelper(this).EnsureHandle();
        Reposition();
        _inputStarted = Stopwatch.GetTimestamp();
        Show();
        Reposition();
        // focus a neutral element, never an acknowledgment action.
        Heading.Focus();
    }
    internal void ShowCompact()
    {
        _compact = true;
        DetailsPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Visible;
        Reposition();
    }
    internal void Reposition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var bounds = WindowInterop.GetMonitorBounds(_monitor);
        if (bounds.IsEmpty)
        {
            _monitor = WindowInterop.MonitorFromWindow(WindowInterop.GetForegroundWindow(), 2);
            bounds = WindowInterop.GetMonitorBounds(_monitor);
        }
        if (bounds.IsEmpty) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = _compact ? Math.Min(bounds.Width, 460 * dpi.DpiScaleX) : bounds.Width;
        var height = _compact ? Math.Min(bounds.Height, 200 * dpi.DpiScaleY) : bounds.Height;
        var left = _compact ? Math.Max(bounds.Left, bounds.Right - width - 16) : bounds.Left;
        var top = _compact ? bounds.Top + Math.Min(16, Math.Max(0, bounds.Height - height)) : bounds.Top;
        WindowInterop.SetWindowPos(handle, WindowInterop.HWND_TOPMOST, (int)left, (int)top,
            (int)width, (int)height, WindowInterop.SWP_NOACTIVATE);
    }
    internal void CloseForShutdown()
    {
        if (_closed) return;
        _allowClose = true;
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _viewModel.Entries.Count > 0)
        {
            e.Cancel = true;
            _viewModel.ShowMessage("Acknowledge each reminder, or use Show smaller to keep working. You can still exit Noted from its tray menu.");
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
    private bool IsInputReady => IsActive && Keyboard.Modifiers == ModifierKeys.None &&
        Stopwatch.GetElapsedTime(_inputStarted) >= TimeSpan.FromMilliseconds(500);
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsInputReady || e.ClickCount > 1) e.Handled = true;
        base.OnPreviewMouseLeftButtonDown(e);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape || ((e.Key == Key.Enter || e.Key == Key.Space) && (!IsInputReady || e.IsRepeat)))
            e.Handled = true;
        base.OnPreviewKeyDown(e);
    }
    private static ChecklistUrgentReminderEntry? Entry(object sender) => (sender as FrameworkElement)?.DataContext as ChecklistUrgentReminderEntry;
    private void OpenTask_Click(object sender, RoutedEventArgs e)
    {
        if (IsInputReady && Entry(sender) is { } entry) _viewModel.OpenTask(entry);
    }
    private void Silence_Click(object sender, RoutedEventArgs e)
    {
        if (IsInputReady && Entry(sender) is { } entry) _viewModel.TrySilence(entry);
    }
    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInputReady || Entry(sender) is not { } entry) return;
        if (_viewModel.TryAcknowledge(entry))
        {
            _inputStarted = Stopwatch.GetTimestamp();
            Heading.Focus();
        }
    }
    private void ShowSmaller_Click(object sender, RoutedEventArgs e) => ShowCompact();
    private void ReturnToAlert_Click(object sender, RoutedEventArgs e)
    {
        _compact = false;
        CompactPanel.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;
        _inputStarted = Stopwatch.GetTimestamp();
        Reposition();
        Heading.Focus();
    }
}
