using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Noted.Helpers;

namespace Noted;

public abstract class OverlayWindow : Window
{
    private bool _isChangingGroupVisibility;
    private WindowEdgeResizer? _edgeResizer;

    protected bool _ghostModeEnabled;
    protected double _ghostModeOpacity;
    protected double _defaultOpacity;

    protected void InitializeOverlay(bool ghostModeEnabled, double ghostModeOpacity, double defaultOpacity)
    {
        _edgeResizer ??= new WindowEdgeResizer(this, SaveWindowState);
        // Retain legacy state fields for persistence; WindowAppearance owns the live opacity.
        _ghostModeEnabled = ghostModeEnabled;
        _ghostModeOpacity = NormalizeOpacity(ghostModeOpacity, 0.25);
        _defaultOpacity = NormalizeOpacity(defaultOpacity, 0.88);

        Loaded += OnOverlayLoaded;
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

    private void OnOverlayLoaded(object sender, RoutedEventArgs e)
    {
        if (FindName("TitleBar") is Border titleBar)
            titleBar.MouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
    }

    private void OnOverlayClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VisualTreeHelpers.FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;

        if (e.ClickCount == 2)
        {
            RequestHide();
            return;
        }

        try
        {
            DragMove();
            SaveWindowState();
        }
        catch { }
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

    internal bool IsWindowVisible => Visibility == Visibility.Visible;
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
        Visibility = Visibility.Visible;
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
