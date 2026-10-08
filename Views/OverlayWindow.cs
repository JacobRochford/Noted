using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Noted.Helpers;
using Noted.Services;

namespace Noted;

public abstract class OverlayWindow : Window
{
    internal virtual string AppearanceSettingsKey => GetType().Name;
    private bool _isChangingGroupVisibility;
    private WindowEdgeResizer? _edgeResizer;
    private WindowState _restoreWindowState = WindowState.Normal;

    protected bool _ghostModeEnabled;
    protected double _ghostModeOpacity;
    protected double _defaultOpacity;

    protected void InitializeOverlay(bool ghostModeEnabled, double ghostModeOpacity, double defaultOpacity)
    {
        _edgeResizer ??= new WindowEdgeResizer(this, SaveWindowState);
        PreviewMouseLeftButtonDown += Background_MouseLeftButtonDown;
        AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(Background_QueryCursor), true);
        // Retain legacy state fields for persistence; WindowAppearance owns the live opacity.
        _ghostModeEnabled = ghostModeEnabled;
        _ghostModeOpacity = NormalizeOpacity(ghostModeOpacity, 0.25);
        _defaultOpacity = NormalizeOpacity(defaultOpacity, 0.88);

        Closing += OnOverlayClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowInterop.EnsureToolWindow(new WindowInteropHelper(this).Handle);
    }

    protected void RestoreWindowBounds(double left, double top, double width, double height)
    {
        var bounds = WindowInterop.NormalizeWindowBounds(
            left,
            top,
            width,
            height,
            MinWidth,
            MinHeight,
            Width,
            Height);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void OnOverlayClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();
    }

    private void Background_QueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (IsNearResizeEdge(Mouse.GetPosition(this)))
            return;

        WindowDragHitTest.QueryCursor(e.OriginalSource as DependencyObject, this, e);
    }

    private bool IsNearResizeEdge(Point point) => WindowState == WindowState.Normal &&
        ResizeEdgeHitTest.Find(point, ActualWidth, ActualHeight, ResizeEdgeHitTest.DefaultThickness) != ResizeEdge.None;

    private void Background_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || Mouse.Captured is not null || IsNearResizeEdge(e.GetPosition(this)) ||
            !WindowDragHitTest.CanDrag(e.OriginalSource as DependencyObject, this))
            return;

        if (e.ClickCount == 2)
        {
            if (FindName("TitleBar") is Border titleBar &&
                WindowDragHitTest.CanDrag(e.OriginalSource as DependencyObject, titleBar))
            {
                e.Handled = true;
                RequestHide();
            }
            return;
        }

        e.Handled = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException ex) when (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            ExceptionDiagnostics.Record(ex);
            return;
        }

        SaveWindowState();
    }

    protected void ApplyGhostMode(bool enabled)
    {
        _ghostModeEnabled = enabled;
    }

    private static double NormalizeOpacity(double opacity, double fallback) =>
        double.IsNaN(opacity) || double.IsInfinity(opacity)
            ? fallback
            : Math.Clamp(opacity, 0, 1);

    protected abstract void SaveWindowState();
    protected abstract void RequestHide();

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            _restoreWindowState = WindowState;
        base.OnStateChanged(e);
    }

    internal bool IsWindowVisible => IsVisible && WindowState != WindowState.Minimized;
    internal bool IsHiddenTogether { get; private set; }
    protected bool IsChangingGroupVisibility => _isChangingGroupVisibility;

    internal void ShowWindow()
    {
        IsHiddenTogether = false;
        ShowWindowCore();
    }

    internal void HideWindow()
    {
        IsHiddenTogether = false;
        Visibility = Visibility.Collapsed;
    }

    internal void HideTogether()
    {
        _isChangingGroupVisibility = true;
        IsHiddenTogether = true;
        try
        {
            Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isChangingGroupVisibility = false;
        }
    }

    internal void RestoreTogether()
    {
        _isChangingGroupVisibility = true;
        try
        {
            ShowWindowCore();
            IsHiddenTogether = false;
        }
        finally
        {
            _isChangingGroupVisibility = false;
        }
    }

    private void ShowWindowCore()
    {
        // Show synchronously so the native handle exists before requesting activation
        WindowInterop.RestoreWindow(this, _restoreWindowState);
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.StripNoActivate(hwnd);
        WindowInterop.SetForegroundWindow(hwnd);
    }

    internal void ToggleWindow()
    {
        if (IsWindowVisible) HideWindow();
        else ShowWindow();
    }
}
