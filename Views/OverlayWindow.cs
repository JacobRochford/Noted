using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Noted.Helpers;

namespace Noted;

public abstract class OverlayWindow : Window
{
    private bool _isChangingGroupVisibility;

    protected bool _ghostModeEnabled;
    protected double _ghostModeOpacity;
    protected double _defaultOpacity;

    protected void InitializeOverlay(bool ghostModeEnabled, double ghostModeOpacity, double defaultOpacity)
    {
        _ghostModeEnabled = ghostModeEnabled;
        _ghostModeOpacity = NormalizeOpacity(ghostModeOpacity, 0.25);
        _defaultOpacity = NormalizeOpacity(defaultOpacity, 0.88);
        Opacity = ghostModeEnabled ? _ghostModeOpacity : _defaultOpacity;

        Loaded += OnOverlayLoaded;
        Closing += OnOverlayClosing;
        MouseEnter += OnOverlayMouseEnter;
        MouseLeave += OnOverlayMouseLeave;
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

    private void OnOverlayMouseEnter(object sender, MouseEventArgs e)
    {
        if (_ghostModeEnabled)
            AnimateOpacity(_defaultOpacity);
    }

    private void OnOverlayMouseLeave(object sender, MouseEventArgs e)
    {
        if (_ghostModeEnabled)
            AnimateOpacity(_ghostModeOpacity);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source is Button) return;

        if (e.ClickCount == 2)
        {
            OnTitleBarDoubleClick();
            return;
        }

        try
        {
            DragMove();
            SaveWindowState();
        }
        catch { }
    }

    protected virtual void OnTitleBarDoubleClick() { }

    protected void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Width + e.HorizontalChange >= MinWidth)
            Width += e.HorizontalChange;
        if (Height + e.VerticalChange >= MinHeight)
            Height += e.VerticalChange;
    }

    protected void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveWindowState();
    }

    protected void ApplyGhostMode(bool enabled)
    {
        _ghostModeEnabled = enabled;
        AnimateOpacity(enabled && !IsMouseOver ? _ghostModeOpacity : _defaultOpacity);
    }

    protected void AnimateOpacity(double opacity)
    {
        var animation = new DoubleAnimation(Opacity, opacity, TimeSpan.FromMilliseconds(300));
        BeginAnimation(OpacityProperty, animation);
    }

    private static double NormalizeOpacity(double opacity, double fallback) =>
        double.IsNaN(opacity) || double.IsInfinity(opacity)
            ? fallback
            : Math.Clamp(opacity, 0, 1);

    protected abstract void SaveWindowState();

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
