using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.ViewModels;

namespace Noted;

public partial class JotCaptureWindow : Window
{
    private readonly JotCaptureViewModel _viewModel;
    private IntPtr _previousForeground;
    private bool _isComposing;
    private bool _contextMenuOpen;
    private bool _shutdownPrepared;

    internal JotCaptureWindow(JotCaptureViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        TextCompositionManager.AddPreviewTextInputStartHandler(CaptureTextBox, (_, _) => _isComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(CaptureTextBox, (_, _) =>
        {
            _isComposing = false;
            HideIfInactive();
        });
        CaptureTextBox.ContextMenuOpening += (_, _) => _contextMenuOpen = true;
        CaptureTextBox.ContextMenuClosing += (_, _) =>
        {
            _contextMenuOpen = false;
            HideIfInactive();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowInterop.EnsureToolWindow(new WindowInteropHelper(this).Handle);
    }

    internal void ShowCapture()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var foreground = WindowInterop.GetForegroundWindow();
        if (foreground != handle)
            _previousForeground = foreground;

        // position while hidden to avoid flashing on the previous monitor.
        new WindowInteropHelper(this).EnsureHandle();
        UpdateLayout();
        WindowInterop.CenterOnForegroundMonitor(this, _previousForeground);
        Show();
        UpdateLayout();
        WindowInterop.CenterOnForegroundMonitor(this, _previousForeground);
        handle = new WindowInteropHelper(this).Handle;
        WindowInterop.StripNoActivate(handle);
        WindowInterop.SetForegroundWindow(handle);
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible || !IsActive)
                return;
            CaptureTextBox.Focus();
            Keyboard.Focus(CaptureTextBox);
            CaptureTextBox.CaretIndex = CaptureTextBox.Text.Length;
        }));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsVisible && !_isComposing && e.Key != Key.ImeProcessed)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                if (!e.IsRepeat && _viewModel.TrySave())
                    Dismiss(restoreFocus: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss(restoreFocus: true);
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        HideIfInactive();
    }

    private void HideIfInactive()
    {
        // context menus and IME candidate windows can briefly take focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible && !IsActive && !_contextMenuOpen && !_isComposing)
                Dismiss(restoreFocus: false);
        }));
    }

    private void Dismiss(bool restoreFocus)
    {
        if (!_viewModel.TryFlushDraft(out _))
            return;
        var wasForeground = WindowInterop.GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        Hide();
        if (restoreFocus && wasForeground && WindowInterop.IsWindow(_previousForeground))
            WindowInterop.SetForegroundWindow(_previousForeground);
    }

    private void OpenJots_Click(object sender, RoutedEventArgs e)
    {
        Dismiss(restoreFocus: false);
        if (!IsVisible)
            Services.WindowManager.ShowJots();
    }

    internal bool TryFlushDraft(out string? error) => _viewModel.TryFlushDraft(out error);
    internal void PrepareForShutdown() => _shutdownPrepared = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownPrepared)
        {
            e.Cancel = true;
            Dismiss(restoreFocus: true);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
