using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Noted.Models;
using Noted.Helpers;
using Noted.Services;
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
    private readonly ChecklistNotificationOptions _options;
    private readonly DispatcherTimer _holdTimer;
    private ChecklistUrgentReminderEntry? _holding;
    private FrameworkElement? _holdControl;
    private bool _mouseHolding;

    internal ChecklistUrgentReminderWindow(ChecklistUrgentRemindersViewModel viewModel, IntPtr monitor = default, ChecklistNotificationOptions? options = null)
    {
        _viewModel = viewModel;
        _monitor = monitor;
        _options = (options ?? viewModel.Entries.FirstOrDefault()?.Options ?? new()).Normalize();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _holdTimer.Tick += HoldTick;
        Deactivated += WindowDeactivated;
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        InitializeComponent();
        DataContext = viewModel;
        UpdateEffects();
    }
    internal IntPtr TargetMonitor => _monitor;
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
        if (IsActive) Heading.Focus();
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
        var placement = CalculateBounds(bounds, dpi.DpiScaleX, dpi.DpiScaleY, _options, _compact);
        var left = placement.Left; var top = placement.Top; var width = placement.Width; var height = placement.Height;
        WindowInterop.SetWindowPos(handle, WindowInterop.HWND_TOPMOST, (int)left, (int)top,
            (int)width, (int)height, WindowInterop.SWP_NOACTIVATE);
    }
    internal static Rect CalculateBounds(Rect bounds, double dpiX, double dpiY, ChecklistNotificationOptions options, bool compact)
    {
        options = options.Normalize();
        var fraction = options.Size == ChecklistAlertSize.FullScreen ? 1 : options.ScreenPercent / 100d;
        var width = compact ? Math.Min(bounds.Width, 460 * dpiX) : options.Size == ChecklistAlertSize.Large
            ? Math.Min(bounds.Width * 0.9, 1100 * dpiX) : bounds.Width * fraction;
        var height = compact ? Math.Min(bounds.Height, 200 * dpiY) : options.Size == ChecklistAlertSize.Large
            ? Math.Min(bounds.Height * 0.9, 800 * dpiY) : bounds.Height * fraction;
        return new Rect(compact ? Math.Max(bounds.Left, bounds.Right - width - 16) : bounds.Left + (bounds.Width - width) / 2,
            compact ? bounds.Top + Math.Min(16, Math.Max(0, bounds.Height - height)) : bounds.Top + (bounds.Height - height) / 2,
            width, height);
    }

    internal void CloseForShutdown()
    {
        if (_closed) return;
        _allowClose = true;
        Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && !_viewModel.IsPreview && _viewModel.Entries.Count > 0)
        {
            e.Cancel = true;
            _viewModel.ShowMessage("Acknowledge each reminder, or use Show smaller to keep working. You can still exit Noted from its tray menu.");
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        CancelHold();
        _holdTimer.Tick -= HoldTick;
        Deactivated -= WindowDeactivated;
        SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
        PulseAccent.BeginAnimation(OpacityProperty, null);
        base.OnClosed(e);
    }
    private bool IsInputReady => _viewModel.IsPreview || IsActive && Keyboard.Modifiers == ModifierKeys.None &&
        Stopwatch.GetElapsedTime(_inputStarted) >= TimeSpan.FromMilliseconds(500);
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsInputReady || e.ClickCount > 1) e.Handled = true;
        base.OnPreviewMouseLeftButtonDown(e);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_viewModel.IsPreview && e.Key == Key.Escape) { CloseForShutdown(); e.Handled = true; return; }
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
    private void StopPreview_Click(object sender, RoutedEventArgs e) { if (_viewModel.IsPreview) CloseForShutdown(); }
    private void SystemPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!_closed) UpdateEffects(); });
    }
    private void UpdateEffects()
    {
        WindowAppearance.ApplyNotificationContrast(this, SystemParameters.HighContrast);
        PulseAccent.BeginAnimation(OpacityProperty, null);
        PulseAccent.Opacity = 1;
        if (_options.PulseAccent && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            PulseAccent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(2.5))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void BeginHold(object sender, bool mouse)
    {
        if (!IsInputReady || Entry(sender) is not { } entry) return;
        CancelHold();
        var control = sender as FrameworkElement;
        control?.Focus();
        _holding = entry; _holdControl = control; _mouseHolding = mouse;
        _viewModel.BeginHold(entry); _holdTimer.Start();
    }
    private void HoldTick(object? sender, EventArgs e) { if (_holding is { } entry) _viewModel.UpdateHold(entry); }
    private void EndHold(bool complete)
    {
        var entry = _holding;
        _holdTimer.Stop(); _holding = null; _mouseHolding = false;
        var control = _holdControl; _holdControl = null;
        if (complete && entry is not null && _viewModel.FinishHold(entry)) { _inputStarted = Stopwatch.GetTimestamp(); Heading.Focus(); }
        else _viewModel.CancelHolds();
        if (control?.IsMouseCaptured == true) control.ReleaseMouseCapture();
    }
    private void CancelHold() => EndHold(false);
    private void WindowDeactivated(object? sender, EventArgs e) => CancelHold();
    private void HoldFocusLost(object sender, KeyboardFocusChangedEventArgs e) { if (_holding is not null) CancelHold(); }
    private void HoldCaptureLost(object sender, MouseEventArgs e) { if (_holding is not null) CancelHold(); }
    private void HoldMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginHold(sender, mouse: true);
        if (_holding is not null && _holdControl?.CaptureMouse() != true) CancelHold();
    }
    private void HoldMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_mouseHolding) return;
        var control = sender as FrameworkElement;
        var point = control is null ? new Point(-1, -1) : e.GetPosition(control);
        EndHold(control is not null && point.X >= 0 && point.Y >= 0 && point.X <= control.ActualWidth && point.Y <= control.ActualHeight);
    }
    private void HoldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space && e.Key != Key.Enter) return;
        e.Handled = true;
        if (!e.IsRepeat && e.Key == Key.Space) BeginHold(sender, mouse: false);
    }
    private void HoldKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { e.Handled = true; if (!_mouseHolding) EndHold(IsInputReady); }
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
