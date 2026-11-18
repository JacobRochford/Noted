using System.Windows;
using System.Windows.Threading;
using Noted.Models;
using Noted.ViewModels;

namespace Noted.Services;

internal sealed class ChecklistNotificationPreview : IDisposable
{
    private readonly Window _ownerWindow;
    private readonly Func<ChecklistNotificationStyle, ChecklistNotificationOptions, uint> _showWindows;
    private readonly Action<uint> _stopWindows;
    private uint _notificationId;
    private bool _disposed;
    private ChecklistWindowViewModel? _owner;
    private ChecklistUrgentReminderPresenter? _presenter;
    private ChecklistUrgentRemindersViewModel? _soundModel;
    private ChecklistUrgentReminderAudio? _audio;
    internal ChecklistNotificationPreview(Window ownerWindow,
        Func<ChecklistNotificationStyle, ChecklistNotificationOptions, uint>? showWindows = null, Action<uint>? stopWindows = null)
    {
        _ownerWindow = ownerWindow;
        _showWindows = showWindows ?? ((style, options) => Application.Current is App app ? app.PreviewChecklistWindowsNotification(style, options) : 0);
        _stopWindows = stopWindows ?? (id => { if (Application.Current is App app) app.StopChecklistNotificationPreview(id); });
    }
    internal bool Show(ChecklistNotificationStyle style, ChecklistNotificationOptions options, out string? error, bool soundOnly = false)
    {
        error = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (style != ChecklistNotificationStyle.Urgent)
        {
            _notificationId = _showWindows(style, options);
            if (_notificationId != 0) return true;
            error = "Windows could not submit this preview. Check notification permissions and run Noted without administrator elevation.";
            return false;
        }
        _owner = new(new MemoryStore(), Dispatcher.CurrentDispatcher, action => action(), false);
        var task = new ChecklistItem { Text = "[Preview] Important reminder", Notes = "Sample reminder details. Stop preview closes this alert immediately.",
            NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) };
        task.ReminderOccurrence = task.ReminderOccurrence! with { Options = options.Normalize() };
        _owner.Items.Add(task);
        if (soundOnly)
        {
            _soundModel = new(_owner, Dispatcher.CurrentDispatcher, isPreview: true);
            _soundModel.TryAccept([new(task.Id, task.ReminderAtUtc!.Value, task.Text, Style: ChecklistNotificationStyle.Urgent,
                Occurrence: task.ReminderOccurrence, Options: task.ReminderOccurrence.Options)]);
            _audio = new(_soundModel, Dispatcher.CurrentDispatcher,
                () => _ownerWindow.IsVisible && ChecklistUrgentReminderPresenter.IsInteractiveDesktopAvailable(), soundOnly: true);
            _audio.Refresh();
            error = _soundModel.StatusMessage;
            return error is null;
        }
        _presenter = new(_owner, Dispatcher.CurrentDispatcher, _ => { }, isPreview: true, previewOwner: _ownerWindow);
        if (_presenter.TrySubmit([new(task.Id, task.ReminderAtUtc!.Value, task.Text, Style: ChecklistNotificationStyle.Urgent,
            Occurrence: task.ReminderOccurrence, Options: options)])) return true;
        error = "The preview cannot be displayed on the current desktop.";
        Dispose(); return false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audio?.Dispose(); _audio = null; _soundModel?.Dispose(); _soundModel = null;
        _presenter?.Dispose(); _presenter = null; _owner?.Dispose(); _owner = null;
        var notificationId = _notificationId; _notificationId = 0;
        if (notificationId != 0) _stopWindows(notificationId);
    }
    private sealed class MemoryStore : IChecklistContentService
    {
        public ChecklistContentLoadResult LoadItems() => new([], [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items) => new(null);
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
