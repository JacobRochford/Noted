using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Noted.Services;

namespace Noted.Helpers;

internal sealed record TaskbarInfo(IntPtr Handle, Rect Bounds, Rect NotificationArea, Rect MonitorBounds);

internal static class TaskbarButtonPlacement
{
    internal static TaskbarInfo? FindTaskbar(DisplayInfo display)
    {
        var monitor = WindowInterop.GetMonitorForBounds(
            new Rect(display.WorkLeft, display.WorkTop, display.WorkWidth, display.WorkHeight));
        if (monitor == IntPtr.Zero)
            return null;

        // shell window classes are not a public contract; fall back if Windows changes them.
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var handle = IntPtr.Zero;
            while ((handle = FindWindowEx(IntPtr.Zero, handle, className, null)) != IntPtr.Zero)
            {
                if (!IsWindowVisible(handle) || WindowInterop.MonitorFromWindow(handle, 0) != monitor)
                    continue;

                var bounds = WindowInterop.GetWindowBounds(handle);
                var notificationArea = Rect.Empty;
                EnumChildWindows(handle, (child, _) =>
                {
                    var name = new StringBuilder(128);
                    GetClassName(child, name, name.Capacity);
                    var childClass = name.ToString();
                    if ((childClass != "TrayNotifyWnd" && childClass != "TrayClockWClass" && childClass != "ClockButton") ||
                        !IsWindowVisible(child))
                        return true;

                    var childBounds = WindowInterop.GetWindowBounds(child);
                    if (!childBounds.IsEmpty && childBounds.IntersectsWith(bounds))
                        notificationArea.Union(childBounds);
                    return true;
                }, IntPtr.Zero);

                return new TaskbarInfo(handle, bounds, notificationArea, WindowInterop.GetMonitorBounds(monitor));
            }
        }
        return null;
    }

    internal static Rect CalculateBounds(Rect taskbar, Rect monitor, Rect notificationArea, Size button, bool onLeft)
    {
        if (taskbar.IsEmpty || monitor.IsEmpty || taskbar.Width <= taskbar.Height)
            return Rect.Empty;

        var visible = Rect.Intersect(taskbar, monitor);
        var height = Math.Min(button.Height, visible.Height - 4);
        if (visible.IsEmpty || height < 24 || visible.Width < button.Width + 16)
            return Rect.Empty;

        var left = visible.Left + 8;
        if (!onLeft)
        {
            // do not guess at the clock/tray width and cover Windows controls.
            if (notificationArea.IsEmpty || !notificationArea.IntersectsWith(visible))
                return Rect.Empty;
            left = notificationArea.Left - button.Width - 8;
            if (left < visible.Left + 8)
                return Rect.Empty;
        }

        return new Rect(left, visible.Top + (visible.Height - height) / 2, button.Width, height);
    }

    private delegate bool EnumChildProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
