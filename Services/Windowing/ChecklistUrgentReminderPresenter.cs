using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Noted.ViewModels;

namespace Noted.Services;

internal sealed class ChecklistUrgentReminderPresenter : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<Guid> _openTask;
    private readonly Func<bool> _canPresent;
    private readonly DispatcherTimer _displayTimer;
    private ChecklistUrgentReminderWindow? _window;
    private bool _appSuspended;
    private bool _powerSuspended;
    private bool _sessionLocked;
    private bool _accepting;
    private bool _disposed;

    internal ChecklistUrgentReminderPresenter(ChecklistWindowViewModel owner, Dispatcher dispatcher,
        Action<Guid> openTask, Func<bool>? canPresent = null)
    {
        _dispatcher = dispatcher;
        _openTask = openTask;
        _canPresent = canPresent ?? IsInteractiveDesktopAvailable;
        ViewModel = new(owner, dispatcher);
        ViewModel.Changed += ViewChanged;
        ViewModel.OpenTaskRequested += OpenTask;
        // this updates displayed elapsed time and restores alerts after a temporary secure desktop.
        _displayTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _displayTimer.Tick += DisplayTick;
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }
    internal ChecklistUrgentRemindersViewModel ViewModel { get; }
    internal bool TrySubmit(IReadOnlyList<ChecklistReminder> reminders)
    {
        _dispatcher.VerifyAccess();
        if (!CanPresent()) return false;
        try
        {
            _accepting = true;
            if (!ViewModel.TryAccept(reminders)) return false;
        }
        finally { _accepting = false; }
        UpdatePresentation();
        return _window?.IsVisible == true;
    }
    internal void SetSuspended(bool suspended)
    {
        _dispatcher.VerifyAccess();
        _appSuspended = suspended;
        UpdatePresentation();
    }
    private bool CanPresent() => !_disposed && !_appSuspended && !_powerSuspended && !_sessionLocked && _canPresent();
    private void ViewChanged(object? sender, EventArgs e)
    {
        if (!_accepting) UpdatePresentation();
    }
    private void UpdatePresentation()
    {
        if (_disposed) return;
        if (ViewModel.Entries.Count == 0)
        {
            _displayTimer.Stop();
            _window?.CloseForShutdown();
            return;
        }
        _displayTimer.Start();
        if (!CanPresent()) { _window?.Hide(); return; }
        try
        {
            if (_window is null)
            {
                _window = new(ViewModel);
                _window.Closed += WindowClosed;
            }
            _window.ShowAlert();
        }
        catch (Exception exception)
        {
            ExceptionDiagnostics.Record(exception, "Present urgent Checklist reminders");
            ViewModel.ShowMessage("The urgent alert could not be displayed. Noted will retry.");
            _window?.CloseForShutdown();
        }
    }
    private void OpenTask(Guid taskId)
    {
        // retain a compact return-to-alert window so Checklist can be used without acknowledgment.
        _window?.ShowCompact();
        _openTask(taskId);
    }
    private void WindowClosed(object? sender, EventArgs e)
    {
        if (sender is ChecklistUrgentReminderWindow window) window.Closed -= WindowClosed;
        _window = null;
    }
    private void DisplayTick(object? sender, EventArgs e) => ViewModel.Refresh();
    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (!_disposed) action(); });
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatch(() =>
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _sessionLocked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _sessionLocked = false;
                break;
        }
        ViewModel.Refresh();
    });
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatch(() =>
    {
        if (e.Mode == PowerModes.Suspend) _powerSuspended = true;
        else if (e.Mode == PowerModes.Resume) _powerSuspended = false;
        ViewModel.Refresh();
    });
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatch(() => _window?.Reposition());

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder information, uint length, out uint needed);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    private static bool IsInteractiveDesktopAvailable()
    {
        var desktop = OpenInputDesktop(0, false, 0x1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformation(desktop, 2, name, (uint)(name.Capacity * sizeof(char)), out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= SessionSwitch;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _displayTimer.Stop();
        _displayTimer.Tick -= DisplayTick;
        ViewModel.Changed -= ViewChanged;
        ViewModel.OpenTaskRequested -= OpenTask;
        _window?.CloseForShutdown();
        ViewModel.Dispose();
    }
}
