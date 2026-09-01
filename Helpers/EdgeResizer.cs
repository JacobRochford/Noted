using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Noted.Helpers;

[Flags]
internal enum ResizeEdge
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

internal static class ResizeEdgeHitTest
{
    internal const double DefaultThickness = 6;

    internal static ResizeEdge Find(Point point, double width, double height, double thickness)
    {
        if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height)
            return ResizeEdge.None;

        var edge = ResizeEdge.None;
        if (point.X <= thickness)
            edge |= ResizeEdge.Left;
        else if (point.X >= width - thickness)
            edge |= ResizeEdge.Right;

        if (point.Y <= thickness)
            edge |= ResizeEdge.Top;
        else if (point.Y >= height - thickness)
            edge |= ResizeEdge.Bottom;

        return edge;
    }

    internal static Cursor? GetCursor(ResizeEdge edge)
    {
        return edge switch
        {
            ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
            ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
            ResizeEdge.Left | ResizeEdge.Top => Cursors.SizeNWSE,
            ResizeEdge.Right | ResizeEdge.Bottom => Cursors.SizeNWSE,
            ResizeEdge.Right | ResizeEdge.Top => Cursors.SizeNESW,
            ResizeEdge.Left | ResizeEdge.Bottom => Cursors.SizeNESW,
            _ => null
        };
    }
}

internal sealed class WindowEdgeResizer : IDisposable
{
    private const uint SwpNoZOrder = 0x0004;

    private readonly Window _window;
    private readonly Action _resizeCompleted;
    private readonly Cursor? _normalCursor;
    private ResizeEdge _activeEdge;
    private NativePoint _startingPointer;
    private NativeRect _startingBounds;
    private int _minimumWidth;
    private int _minimumHeight;
    private int? _maximumWidth;
    private int? _maximumHeight;
    private bool _disposed;

    internal WindowEdgeResizer(Window window, Action resizeCompleted)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(resizeCompleted);

        _window = window;
        _resizeCompleted = resizeCompleted;
        _normalCursor = window.Cursor;

        window.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        window.LostMouseCapture += OnLostMouseCapture;
        window.Closed += OnWindowClosed;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_activeEdge == ResizeEdge.None)
        {
            SetCursor(ResizeEdgeHitTest.Find(
                e.GetPosition(_window),
                _window.ActualWidth,
                _window.ActualHeight,
                ResizeEdgeHitTest.DefaultThickness));
            return;
        }

        if (!GetCursorPos(out var pointer))
            return;

        ApplyResize(pointer);
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _window.WindowState != WindowState.Normal)
            return;

        var edge = ResizeEdgeHitTest.Find(
            e.GetPosition(_window),
            _window.ActualWidth,
            _window.ActualHeight,
            ResizeEdgeHitTest.DefaultThickness);
        if (edge == ResizeEdge.None)
            return;

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero ||
            !GetCursorPos(out _startingPointer) ||
            !GetWindowRect(handle, out _startingBounds))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(_window);
        _minimumWidth = Math.Max(1, (int)Math.Ceiling(_window.MinWidth * dpi.DpiScaleX));
        _minimumHeight = Math.Max(1, (int)Math.Ceiling(_window.MinHeight * dpi.DpiScaleY));
        _maximumWidth = ToPixelLimit(_window.MaxWidth, dpi.DpiScaleX);
        _maximumHeight = ToPixelLimit(_window.MaxHeight, dpi.DpiScaleY);
        _activeEdge = edge;
        Mouse.Capture(_window, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _activeEdge == ResizeEdge.None)
            return;

        e.Handled = true;
        FinishResize(saveState: true);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_activeEdge != ResizeEdge.None)
            FinishResize(saveState: true);
    }

    private void ApplyResize(NativePoint pointer)
    {
        var horizontalChange = pointer.X - _startingPointer.X;
        var verticalChange = pointer.Y - _startingPointer.Y;
        var left = _startingBounds.Left;
        var top = _startingBounds.Top;
        var right = _startingBounds.Right;
        var bottom = _startingBounds.Bottom;

        if (_activeEdge.HasFlag(ResizeEdge.Left))
        {
            left = Math.Min(right - _minimumWidth, left + horizontalChange);
            if (_maximumWidth is int maximumWidth)
                left = Math.Max(right - maximumWidth, left);
        }
        else if (_activeEdge.HasFlag(ResizeEdge.Right))
        {
            right = Math.Max(left + _minimumWidth, right + horizontalChange);
            if (_maximumWidth is int maximumWidth)
                right = Math.Min(left + maximumWidth, right);
        }

        if (_activeEdge.HasFlag(ResizeEdge.Top))
        {
            top = Math.Min(bottom - _minimumHeight, top + verticalChange);
            if (_maximumHeight is int maximumHeight)
                top = Math.Max(bottom - maximumHeight, top);
        }
        else if (_activeEdge.HasFlag(ResizeEdge.Bottom))
        {
            bottom = Math.Max(top + _minimumHeight, bottom + verticalChange);
            if (_maximumHeight is int maximumHeight)
                bottom = Math.Min(top + maximumHeight, bottom);
        }

        var handle = new WindowInteropHelper(_window).Handle;
        WindowInterop.SetWindowPos(
            handle,
            IntPtr.Zero,
            left,
            top,
            right - left,
            bottom - top,
            SwpNoZOrder | WindowInterop.SWP_NOACTIVATE);
    }

    private void FinishResize(bool saveState)
    {
        if (_activeEdge == ResizeEdge.None)
            return;

        _activeEdge = ResizeEdge.None;
        if (Mouse.Captured == _window)
            Mouse.Capture(null);
        _window.Cursor = _normalCursor;

        if (saveState)
            _resizeCompleted();
    }

    private void SetCursor(ResizeEdge edge)
    {
        _window.Cursor = ResizeEdgeHitTest.GetCursor(edge) ?? _normalCursor;
    }

    private static int? ToPixelLimit(double value, double scale)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? null
            : Math.Max(1, (int)Math.Floor(value * scale));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        FinishResize(saveState: false);
        _window.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove));
        _window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown));
        _window.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp));
        _window.LostMouseCapture -= OnLostMouseCapture;
        _window.Closed -= OnWindowClosed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
}

internal sealed class CanvasEdgeResizer : IDisposable
{
    private readonly FrameworkElement _element;
    private readonly Canvas _canvas;
    private readonly double _margin;
    private readonly Cursor? _normalCursor;
    private ResizeEdge _activeEdge;
    private Point _startingPointer;
    private Rect _startingBounds;
    private bool _disposed;

    internal CanvasEdgeResizer(FrameworkElement element, Canvas canvas, double margin = 0)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(canvas);

        _element = element;
        _canvas = canvas;
        _margin = Math.Max(0, margin);
        _normalCursor = element.Cursor;

        element.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove), true);
        element.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        element.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        element.LostMouseCapture += OnLostMouseCapture;
    }

    internal bool IsResizing => _activeEdge != ResizeEdge.None;

    internal bool IsNearEdge(Point point)
    {
        return ResizeEdgeHitTest.Find(
            point,
            _element.ActualWidth,
            _element.ActualHeight,
            ResizeEdgeHitTest.DefaultThickness) != ResizeEdge.None;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_activeEdge == ResizeEdge.None)
        {
            var edge = ResizeEdgeHitTest.Find(
                e.GetPosition(_element),
                _element.ActualWidth,
                _element.ActualHeight,
                ResizeEdgeHitTest.DefaultThickness);
            _element.Cursor = ResizeEdgeHitTest.GetCursor(edge) ?? _normalCursor;
            return;
        }

        ApplyResize(e.GetPosition(_canvas));
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        var edge = ResizeEdgeHitTest.Find(
            e.GetPosition(_element),
            _element.ActualWidth,
            _element.ActualHeight,
            ResizeEdgeHitTest.DefaultThickness);
        if (edge == ResizeEdge.None)
            return;

        var location = _element.TranslatePoint(new Point(0, 0), _canvas);
        _startingBounds = new Rect(
            location.X,
            location.Y,
            _element.ActualWidth,
            _element.ActualHeight);
        _startingPointer = e.GetPosition(_canvas);
        _activeEdge = edge;

        Canvas.SetRight(_element, double.NaN);
        Canvas.SetBottom(_element, double.NaN);
        Canvas.SetLeft(_element, _startingBounds.Left);
        Canvas.SetTop(_element, _startingBounds.Top);
        Mouse.Capture(_element, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _activeEdge == ResizeEdge.None)
            return;

        e.Handled = true;
        FinishResize();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        FinishResize();
    }

    private void ApplyResize(Point pointer)
    {
        var change = pointer - _startingPointer;
        var left = _startingBounds.Left;
        var top = _startingBounds.Top;
        var right = _startingBounds.Right;
        var bottom = _startingBounds.Bottom;
        var availableWidth = Math.Max(1, _canvas.ActualWidth - (_margin * 2));
        var availableHeight = Math.Max(1, _canvas.ActualHeight - (_margin * 2));
        var minimumWidth = Math.Min(_element.MinWidth, availableWidth);
        var minimumHeight = Math.Min(_element.MinHeight, availableHeight);
        var maximumWidth = NormalizeMaximum(_element.MaxWidth);
        var maximumHeight = NormalizeMaximum(_element.MaxHeight);

        if (_activeEdge.HasFlag(ResizeEdge.Left))
        {
            left = Math.Clamp(left + change.X, _margin, right - minimumWidth);
            if (maximumWidth is double widthLimit)
                left = Math.Max(right - widthLimit, left);
        }
        else if (_activeEdge.HasFlag(ResizeEdge.Right))
        {
            right = Math.Clamp(
                right + change.X,
                left + minimumWidth,
                Math.Max(left + minimumWidth, _canvas.ActualWidth - _margin));
            if (maximumWidth is double widthLimit)
                right = Math.Min(left + widthLimit, right);
        }

        if (_activeEdge.HasFlag(ResizeEdge.Top))
        {
            top = Math.Clamp(top + change.Y, _margin, bottom - minimumHeight);
            if (maximumHeight is double heightLimit)
                top = Math.Max(bottom - heightLimit, top);
        }
        else if (_activeEdge.HasFlag(ResizeEdge.Bottom))
        {
            bottom = Math.Clamp(
                bottom + change.Y,
                top + minimumHeight,
                Math.Max(top + minimumHeight, _canvas.ActualHeight - _margin));
            if (maximumHeight is double heightLimit)
                bottom = Math.Min(top + heightLimit, bottom);
        }

        Canvas.SetLeft(_element, left);
        Canvas.SetTop(_element, top);
        _element.Width = right - left;
        _element.Height = bottom - top;
    }

    private void FinishResize()
    {
        if (_activeEdge == ResizeEdge.None)
            return;

        _activeEdge = ResizeEdge.None;
        if (Mouse.Captured == _element)
            Mouse.Capture(null);
        _element.Cursor = _normalCursor;
    }

    private static double? NormalizeMaximum(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        FinishResize();
        _element.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouseMove));
        _element.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown));
        _element.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp));
        _element.LostMouseCapture -= OnLostMouseCapture;
    }
}
