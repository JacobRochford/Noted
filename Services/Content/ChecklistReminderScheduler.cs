using System.Windows.Threading;
using Microsoft.Win32;
using Noted.Models;
using Noted.ViewModels;

namespace Noted.Services;

internal sealed record ChecklistReminder(Guid TaskId, DateTimeOffset ReminderAtUtc, string Text, DateTimeOffset BatchUtc = default,
    ChecklistNotificationStyle Style = ChecklistNotificationStyle.Standard,
    ChecklistReminderOccurrence? Occurrence = null, string Notes = "");

internal sealed class ChecklistReminderScheduler : IDisposable
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_clockCheckInterval = TimeSpan.FromMinutes(1);
    private readonly ChecklistWindowViewModel _model;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<ChecklistReminder>, bool> _submit;
    private readonly TimeProvider _clock;
    private readonly Func<IReadOnlyList<ChecklistReminder>, bool>? _submitUrgent;
    private readonly HashSet<(Guid TaskId, Guid OccurrenceId)> _presentedUrgent = [];
    private long? _failureTimestamp;
    private bool _needsHandledFlush;
    private bool _checking;
    private bool _suspended;
    private bool _disposed;

    internal ChecklistReminderScheduler(
        ChecklistWindowViewModel model,
        Dispatcher dispatcher,
        Func<IReadOnlyList<ChecklistReminder>, bool> submit,
        TimeProvider? clock = null,
        Func<IReadOnlyList<ChecklistReminder>, bool>? submitUrgent = null)
    {
        _model = model;
        _dispatcher = dispatcher;
        _submit = submit;
        _clock = clock ?? TimeProvider.System;
        _submitUrgent = submitUrgent;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Timer_Tick;
        _model.ReminderStateChanged += ReminderStateChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemEvents.TimeChanged += TimeChanged;
        CheckNow();
    }

    internal string? LastError { get; private set; }

    internal void CheckNow()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _suspended || _checking) return;
        _checking = true;
        _timer.Stop();
        try
        {
            if (RetryRemaining() > TimeSpan.Zero) return;
            if (_needsHandledFlush)
            {
                if (!_model.TryFlushPendingItems(out var flushError))
                {
                    Fail(flushError);
                    return;
                }
                _needsHandledFlush = false;
            }

            var now = _clock.GetUtcNow();
            if (!_model.Items.Any(item => IsDue(item, now)))
            {
                LastError = null;
                _failureTimestamp = null;
                return;
            }

            // persist identities and schedules before submitting a delivery request.
            if (!_model.TrySaveItems(null, out var error))
            {
                Fail(error);
                return;
            }
            if (_disposed || _suspended) return;
            now = _clock.GetUtcNow();
            // the persisted submission time also identifies a grouped notification after restart.
            var batchUtc = now;
            var reminders = _model.Items.Where(item => IsDue(item, now))
                .OrderBy(item => item.ReminderAtUtc)
                .Select(item => new ChecklistReminder(item.Id, item.ReminderAtUtc!.Value, item.Text, batchUtc,
                    item.NotificationStyle, item.ReminderOccurrence, item.Notes))
                .ToArray();
            if (reminders.Length == 0) return;

            var failed = false;
            foreach (var group in reminders.GroupBy(reminder => reminder.Style))
            {
                if (_disposed || _suspended) return;
                // grouped activation must identify only this style's accepted delivery.
                while (_model.Items.Any(item => item.ReminderHandledAtUtc == batchUtc))
                    batchUtc = batchUtc.AddTicks(1);
                // an earlier delivery callback may cancel or replace another style's occurrence.
                var currentItems = _model.Items.ToDictionary(item => item.Id);
                var request = group.Where(reminder => currentItems.TryGetValue(reminder.TaskId, out var item) &&
                    IsDue(item, _clock.GetUtcNow()) && item.ReminderAtUtc == reminder.ReminderAtUtc &&
                    item.NotificationStyle == reminder.Style && item.ReminderOccurrence?.Id == reminder.Occurrence?.Id)
                    .Select(reminder => reminder with
                    {
                        BatchUtc = batchUtc,
                        Text = currentItems[reminder.TaskId].Text,
                        Notes = currentItems[reminder.TaskId].Notes,
                        Occurrence = currentItems[reminder.TaskId].ReminderOccurrence
                    }).ToArray();
                if (request.Length == 0) continue;
                bool submitted;
                try
                {
                    submitted = group.Key == ChecklistNotificationStyle.Urgent
                        ? _submitUrgent?.Invoke(request) == true : _submit(request);
                }
                catch (Exception exception)
                {
                    ExceptionDiagnostics.Record(exception, "Checklist reminder submission");
                    submitted = false;
                }
                if (_disposed) return;
                if (!submitted)
                {
                    Fail("Checklist reminders could not be submitted.");
                    failed = true;
                    continue;
                }
                // suspension stops subsequent delivery, but acceptance must still be persisted.
                foreach (var reminder in request)
                    if (reminder.Occurrence is { } occurrence) _presentedUrgent.Add((reminder.TaskId, occurrence.Id));
                if (!_model.TryMarkRemindersHandled(request, batchUtc, out error))
                {
                    _needsHandledFlush = true;
                    Fail(error);
                    failed = true;
                }
            }
            if (!failed)
            {
                _failureTimestamp = null;
                LastError = null;
            }
        }
        finally
        {
            _checking = false;
            ScheduleNext();
        }
    }

    internal void SetSuspended(bool suspended)
    {
        _dispatcher.VerifyAccess();
        _suspended = suspended;
        if (suspended) _timer.Stop();
        else CheckNow();
    }

    private bool IsDue(ChecklistItem item, DateTimeOffset now) =>
        !item.IsChecked && item.ReminderAtUtc <= now &&
        (item.NotificationStyle == ChecklistNotificationStyle.Urgent
            ? item.ReminderOccurrence is { AcknowledgedAtUtc: null } occurrence && !_presentedUrgent.Contains((item.Id, occurrence.Id))
            : item.ReminderHandledAtUtc is null);

    private void Fail(string? error)
    {
        LastError = error;
        _failureTimestamp = _clock.GetTimestamp();
    }

    private TimeSpan RetryRemaining() => _failureTimestamp is { } timestamp
        ? s_retryDelay - _clock.GetElapsedTime(timestamp, _clock.GetTimestamp())
        : TimeSpan.Zero;

    private void ScheduleNext()
    {
        _timer.Stop();
        if (_disposed || _suspended) return;
        var next = _model.Items.Where(item => !item.IsChecked &&
            (item.NotificationStyle == ChecklistNotificationStyle.Urgent
                ? item.ReminderOccurrence is { AcknowledgedAtUtc: null } occurrence && !_presentedUrgent.Contains((item.Id, occurrence.Id))
                : item.ReminderHandledAtUtc is null))
            .Select(item => item.ReminderAtUtc).Min();
        if (next is null && !_needsHandledFlush) return;
        var delay = next is { } due ? due - _clock.GetUtcNow() : s_retryDelay;
        var retry = RetryRemaining();
        if (retry > delay) delay = retry;
        if (delay > s_clockCheckInterval) delay = s_clockCheckInterval;
        _timer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void ReminderStateChanged(object? sender, EventArgs e)
    {
        if (!_checking) ScheduleNext();
    }

    private void Timer_Tick(object? sender, EventArgs e) => CheckNow();

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) RequestCheck();
    }

    private void TimeChanged(object? sender, EventArgs e) => RequestCheck();

    private void RequestCheck()
    {
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(CheckNow, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _model.ReminderStateChanged -= ReminderStateChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemEvents.TimeChanged -= TimeChanged;
    }
}
