using System.Runtime.InteropServices;

namespace MyNotes.Helpers;

internal static class WindowInterop {
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
}
