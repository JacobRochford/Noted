using System.Runtime.InteropServices;

namespace Noted.Helpers;

internal static class WindowInterop {
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
}
