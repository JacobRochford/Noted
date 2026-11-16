using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Noted.ViewModels;
using Noted.Models;
using Noted.Helpers;
using System.Windows;

namespace Noted.Services;

internal sealed class ChecklistUrgentReminderPresenter : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ChecklistWindowViewModel _owner;
    private readonly Window? _previewOwner;
    private readonly Action<Guid> _openTask;
    private readonly Func<bool> _canPresent;
    private readonly DispatcherTimer _displayTimer;
    private readonly List<ChecklistUrgentReminderWindow> _windows = [];
    private readonly Dictionary<Guid, IntPtr[]> _targets = [];
    private readonly ChecklistUrgentReminderAudio _audio;
    private bool _appSuspended;
    private bool _powerSuspended;
    private bool _sessionLocked;
    private bool _accepting;
    private bool _disposed;

    internal ChecklistUrgentReminderPresenter(ChecklistWindowViewModel owner, Dispatcher dispatcher,
        Action<Guid> openTask, Func<bool>? canPresent = null, bool isPreview = false, Window? previewOwner = null)
    {
        _dispatcher = dispatcher;
        _owner = owner;
        _previewOwner = previewOwner;
        _openTask = openTask;
        _canPresent = canPresent ?? IsInteractiveDesktopAvailable;
        ViewModel = new(owner, dispatcher, isPreview: isPreview);
        ViewModel.Changed += ViewChanged;
        ViewModel.OpenTaskRequested += OpenTask;
        _audio = new(ViewModel, dispatcher, () => CanPresent() && _windows.Any(window => window.IsVisible && window.WindowState != WindowState.Minimized));
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
            if (!_owner.TryCaptureUrgentOptions(reminders, out var error))
            { ViewModel.ShowMessage(error ?? "Reminder options could not be saved."); return false; }
            foreach (var reminder in reminders)
                if (reminder.Occurrence is { } occurrence && !_targets.ContainsKey(occurrence.Id))
                    _targets[occurrence.Id] = ResolveMonitors(reminder.Options ?? new());
            if (!ViewModel.TryAccept(reminders)) return false;
        }
        finally { _accepting = false; }
        UpdatePresentation();
        return _windows.Any(window => window.IsVisible && window.WindowState != WindowState.Minimized);
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
            foreach (var window in _windows.ToArray()) window.CloseForShutdown();
            _audio.Refresh();
            return;
        }
        _displayTimer.Start();
        if (!CanPresent()) { foreach (var window in _windows) window.Hide(); _audio.Refresh(); return; }
        try
        {
            var ids = ViewModel.Entries.Select(entry => entry.OccurrenceId).ToHashSet();
            foreach (var id in _targets.Keys.Where(id => !ids.Contains(id)).ToArray()) _targets.Remove(id);
            var monitors = ViewModel.Entries.SelectMany(entry => _targets.TryGetValue(entry.OccurrenceId, out var targets)
                ? targets : ResolveMonitors(entry.Options))
                .Select(handle => WindowInterop.GetMonitorBounds(handle).IsEmpty ? WindowInterop.MonitorFromWindow(IntPtr.Zero, 2) : handle)
                .Distinct().ToArray();
            foreach (var oldWindow in _windows.Where(window => !monitors.Contains(window.TargetMonitor)).ToArray()) oldWindow.CloseForShutdown();
            if (_disposed) return;
            foreach (var monitor in monitors)
            {
                var window = _windows.FirstOrDefault(window => window.TargetMonitor == monitor);
                if (window is null)
                {
                    var anchor = ViewModel.Entries.FirstOrDefault(entry => _targets.TryGetValue(entry.OccurrenceId, out var target) && target.Contains(monitor))
                        ?? ViewModel.Entries[0];
                    window = new(ViewModel, monitor, anchor.Options) { ShowActivated = _windows.Count == 0, Owner = _previewOwner };
                    window.Closed += WindowClosed;
                    _windows.Add(window);
                }
                window.ShowAlert();
            }
            _audio.Refresh();
        }
        catch (Exception exception)
        {
            ExceptionDiagnostics.Record(exception, "Present urgent Checklist reminders");
            ViewModel.ShowMessage("The urgent alert could not be displayed. Noted will retry.");
            foreach (var window in _windows.ToArray()) window.CloseForShutdown();
            _audio.Stop();
        }
    }
    private void OpenTask(Guid taskId)
    {
        // retain a compact return-to-alert window so Checklist can be used without acknowledgment.
        foreach (var window in _windows) window.ShowCompact();
        _openTask(taskId);
    }
    private void WindowClosed(object? sender, EventArgs e)
    {
        if (sender is ChecklistUrgentReminderWindow window) window.Closed -= WindowClosed;
        if (sender is ChecklistUrgentReminderWindow closed) _windows.Remove(closed);
        if (ViewModel.IsPreview && !_disposed) Dispose();
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
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        _targets.Clear();
        foreach (var window in _windows.ToArray())
            if (WindowInterop.GetMonitorBounds(window.TargetMonitor).IsEmpty) window.CloseForShutdown();
            else window.Reposition();
        UpdatePresentation();
    });
    private static IntPtr[] ResolveMonitors(ChecklistNotificationOptions options)
    {
        var displays = DisplayService.GetDisplays();
        IntPtr Handle(DisplayInfo display) => WindowInterop.GetMonitorForBounds(new Rect(display.WorkLeft, display.WorkTop, display.WorkWidth, display.WorkHeight));
        if (options.Monitor == ChecklistAlertMonitor.All) return displays.Select(Handle).Where(handle => handle != IntPtr.Zero).ToArray();
        if (options.Monitor == ChecklistAlertMonitor.Foreground)
            return [WindowInterop.MonitorFromWindow(WindowInterop.GetForegroundWindow(), 2)];
        var selected = options.Monitor == ChecklistAlertMonitor.Selected
            ? displays.FirstOrDefault(display => display.DeviceName == options.MonitorDeviceName) : null;
        var target = selected ?? displays.FirstOrDefault(display => display.IsPrimary) ?? displays.FirstOrDefault();
        return [target is null ? WindowInterop.MonitorFromWindow(IntPtr.Zero, 2) : Handle(target)];
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder information, uint length, out uint needed);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    internal static bool IsInteractiveDesktopAvailable()
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
        _audio.Dispose();
        foreach (var window in _windows.ToArray()) window.CloseForShutdown();
        ViewModel.Dispose();
    }
}
