using System.Runtime.InteropServices;
using System.Windows;

namespace Noted.Helpers;

internal static class WindowInterop {
    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal int Flags;
    }

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;

    internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    internal const uint SWP_NOSIZE      = 0x0001;
    internal const uint SWP_NOMOVE      = 0x0002;
    internal const uint SWP_NOACTIVATE  = 0x0010;

    internal const int  GWL_EXSTYLE     = -20;
    internal const int  GWL_STYLE       = -16;
    internal const int  WS_EX_TOOLWINDOW = 0x00000080;
    internal const int  WS_EX_NOACTIVATE = 0x08000000;
    internal const int  WS_MINIMIZEBOX   = 0x00020000;
    internal const int  WS_MAXIMIZEBOX   = 0x00010000;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    internal static void StripNoActivate(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_NOACTIVATE);
    }

    internal static void RestoreNoActivate(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
    }

    internal static void RemoveMinimizeAndMaximizeBoxes(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MINIMIZEBOX & ~WS_MAXIMIZEBOX);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    internal static Rect NormalizeWindowBounds(
        double left,
        double top,
        double width,
        double height,
        double minimumWidth,
        double minimumHeight,
        double fallbackWidth,
        double fallbackHeight)
    {
        const double recoveryMargin = 24;

        width = IsFinite(width) && width > 0 ? width : fallbackWidth;
        height = IsFinite(height) && height > 0 ? height : fallbackHeight;
        width = Math.Max(minimumWidth, width);
        height = Math.Max(minimumHeight, height);
        left = IsFinite(left) ? left : SystemParameters.WorkArea.Left + recoveryMargin;
        top = IsFinite(top) ? top : SystemParameters.WorkArea.Top + recoveryMargin;

        var savedBounds = new Rect(left, top, width, height);
        if (!TryGetMonitorWorkArea(savedBounds, out var workArea))
        {
            workArea = SystemParameters.WorkArea;
            left = workArea.Left + recoveryMargin;
            top = workArea.Top + recoveryMargin;
        }

        width = Math.Clamp(width, minimumWidth, Math.Max(minimumWidth, workArea.Width));
        height = Math.Clamp(height, minimumHeight, Math.Max(minimumHeight, workArea.Height));
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new Rect(left, top, width, height);
    }

    private static bool TryGetMonitorWorkArea(Rect bounds, out Rect workArea)
    {
        workArea = default;
        try
        {
            var scale = GetDpiForSystem() / 96d;
            if (!IsFinite(scale) || scale <= 0)
                scale = 1;

            var nativeBounds = new NativeRect
            {
                Left = (int)Math.Floor(bounds.Left * scale),
                Top = (int)Math.Floor(bounds.Top * scale),
                Right = (int)Math.Ceiling(bounds.Right * scale),
                Bottom = (int)Math.Ceiling(bounds.Bottom * scale)
            };
            var monitor = MonitorFromRect(ref nativeBounds, MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero)
                return false;

            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return false;

            workArea = new Rect(
                monitorInfo.Work.Left / scale,
                monitorInfo.Work.Top / scale,
                (monitorInfo.Work.Right - monitorInfo.Work.Left) / scale,
                (monitorInfo.Work.Bottom - monitorInfo.Work.Top) / scale);
            return workArea.Width > 0 && workArea.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
