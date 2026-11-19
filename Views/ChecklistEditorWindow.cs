using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Noted.Helpers;

namespace Noted;

public abstract class ChecklistEditorWindow : Window
{
    private const uint MoveWithoutActivation = WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE | WindowInterop.SWP_NOZORDER;
    private Size _minimum;
    private DispatcherOperation? _updateWorkArea;
    private bool _placedInitially;
    private bool _closed;

    internal void ShowFor(Window? owner)
    {
        if (owner is not null)
            Owner = owner;
        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _minimum = new Size(MinWidth, MinHeight);
        var handle = new WindowInteropHelper(this).Handle;
        var parent = Owner is null ? handle : new WindowInteropHelper(Owner).Handle;
        var work = WindowInterop.GetMonitorWorkArea(WindowInterop.MonitorFromWindow(parent, 2));
        if (!work.IsEmpty)
        {
            var dpi = WindowInterop.GetDpiForWindow(parent);
            SetWorkAreaLimits(work, dpi);
            if (parent != handle)
                MoveIntoWorkArea(handle, work, parent, dpi);
        }
        LocationChanged += (_, _) => QueueWorkAreaUpdate();
        DpiChanged += (_, _) => QueueWorkAreaUpdate();
        SizeChanged += (_, _) =>
        {
            if (_placedInitially && SizeToContent == SizeToContent.Height) FitToWorkArea();
        };
    }

    protected override void OnContentRendered(EventArgs e)
    {
        if (!_placedInitially)
        {
            // place using the HWND size after WPF has sized the content.
            var handle = new WindowInteropHelper(this).Handle;
            var parent = Owner is null ? handle : new WindowInteropHelper(Owner).Handle;
            var work = WindowInterop.GetMonitorWorkArea(WindowInterop.MonitorFromWindow(parent, 2));
            if (!work.IsEmpty)
                MoveIntoWorkArea(handle, work, parent, WindowInterop.GetDpiForWindow(parent));
            _placedInitially = true;
            QueueWorkAreaUpdate();
        }
        base.OnContentRendered(e);
    }

    private void QueueWorkAreaUpdate()
    {
        if (!_placedInitially || _updateWorkArea?.Status == DispatcherOperationStatus.Pending) return;
        _updateWorkArea = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FitToWorkArea(move: false));
    }

    private void FitToWorkArea(bool move = true)
    {
        if (_closed) return;
        var handle = new WindowInteropHelper(this).Handle;
        var work = WindowInterop.GetMonitorWorkArea(WindowInterop.MonitorFromWindow(handle, 2));
        if (work.IsEmpty) return;
        SetWorkAreaLimits(work, WindowInterop.GetDpiForWindow(handle));
        if (move) MoveIntoWorkArea(handle, work);
    }

    private static void MoveIntoWorkArea(IntPtr handle, Rect work, IntPtr parent = default, uint dpi = 0)
    {
        var bounds = WindowInterop.GetWindowBounds(handle);
        if (bounds.IsEmpty) return;

        var owner = parent == IntPtr.Zero || parent == handle ? Rect.Empty : WindowInterop.GetWindowBounds(parent);
        var position = parent == IntPtr.Zero ? ClampToWorkArea(bounds.Location, bounds.Size, work)
            : CalculateInitialPosition(owner, bounds.Size, work, dpi);
        if (position != bounds.Location)
            WindowInterop.SetWindowPos(handle, IntPtr.Zero, (int)position.X, (int)position.Y, 0, 0, MoveWithoutActivation);
    }

    internal static Point CalculateInitialPosition(Rect owner, Size dialog, Rect work, uint dpi)
    {
        var anchor = owner.IsEmpty ? work : owner;
        var gap = Math.Ceiling(12 * DpiScale(dpi));
        var right = anchor.Right + gap;
        var left = anchor.Left - gap - dialog.Width;
        if (right + dialog.Width <= work.Right && right >= work.Left)
            return ClampToWorkArea(new Point(right, anchor.Top), dialog, work);
        if (left >= work.Left && left + dialog.Width <= work.Right)
            return ClampToWorkArea(new Point(left, anchor.Top), dialog, work);

        var center = new Point(anchor.Left + (anchor.Width - dialog.Width) / 2,
            anchor.Top + (anchor.Height - dialog.Height) / 2);
        return ClampToWorkArea(center, dialog, work);
    }

    private static Point ClampToWorkArea(Point point, Size dialog, Rect work) =>
        new(Math.Clamp(point.X, work.Left, Math.Max(work.Left, work.Right - dialog.Width)),
            Math.Clamp(point.Y, work.Top, Math.Max(work.Top, work.Bottom - dialog.Height)));

    private static double DpiScale(uint dpi) => dpi > 0 ? dpi / 96d : 1;

    private void SetWorkAreaLimits(Rect work, uint dpi)
    {
        var scale = DpiScale(dpi);
        MaxWidth = work.Width / scale;
        MaxHeight = work.Height / scale;
        MinWidth = Math.Min(_minimum.Width, MaxWidth);
        MinHeight = Math.Min(_minimum.Height, MaxHeight);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        Close();
    }

    protected void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _updateWorkArea?.Abort();
        base.OnClosed(e);
    }
}
