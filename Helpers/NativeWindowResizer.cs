using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Noted.Helpers;

internal sealed class NativeWindowResizer
{
    private readonly Window _window;
    private readonly HwndSource _source;

    internal NativeWindowResizer(Window window)
    {
        _window = window;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source.AddHook(HitTest);
        window.Closed += (_, _) => _source.RemoveHook(HitTest);
    }

    private IntPtr HitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0084 || _window.WindowState != WindowState.Normal) return IntPtr.Zero;
        var packed = lParam.ToInt64();
        if (!GetWindowRect(hwnd, out var bounds)) return IntPtr.Zero;
        var dpi = VisualTreeHelper.GetDpi(_window);
        var point = new Point(((short)(packed & 0xffff) - bounds.Left) / dpi.DpiScaleX,
            ((short)((packed >> 16) & 0xffff) - bounds.Top) / dpi.DpiScaleY);
        var edge = ResizeEdgeHitTest.Find(point, (bounds.Right - bounds.Left) / dpi.DpiScaleX,
            (bounds.Bottom - bounds.Top) / dpi.DpiScaleY, ResizeEdgeHitTest.DefaultThickness);
        var result = edge switch
        {
            ResizeEdge.Left => 10,
            ResizeEdge.Right => 11,
            ResizeEdge.Top => 12,
            ResizeEdge.Left | ResizeEdge.Top => 13,
            ResizeEdge.Right | ResizeEdge.Top => 14,
            ResizeEdge.Bottom => 15,
            ResizeEdge.Left | ResizeEdge.Bottom => 16,
            ResizeEdge.Right | ResizeEdge.Bottom => 17,
            _ => 0
        };
        handled = result != 0;
        return new IntPtr(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
}
