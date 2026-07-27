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
    protected bool _ghostModeEnabled;
    protected double _ghostModeOpacity;
    protected double _defaultOpacity;

    protected void InitializeOverlay(bool ghostModeEnabled, double ghostModeOpacity, double defaultOpacity)
    {
        _ghostModeEnabled = ghostModeEnabled;
        _ghostModeOpacity = ghostModeOpacity;
        _defaultOpacity = defaultOpacity;
        Opacity = ghostModeEnabled ? ghostModeOpacity : defaultOpacity;

        Loaded += OnOverlayLoaded;
        Closing += OnOverlayClosing;
        MouseEnter += OnOverlayMouseEnter;
        MouseLeave += OnOverlayMouseLeave;
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

    protected abstract void SaveWindowState();

    internal bool IsWindowVisible => Visibility == Visibility.Visible;

    internal void ShowWindow()
    {
        Visibility = Visibility.Visible;
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.StripNoActivate(hwnd);
        WindowInterop.SetForegroundWindow(hwnd);
    }

    internal void HideWindow() => Visibility = Visibility.Collapsed;

    internal void ToggleWindow()
    {
        if (IsWindowVisible) HideWindow();
        else ShowWindow();
    }
}
