using System.Windows.Threading;
using Microsoft.Win32;
using Noted.Models;
using Noted.ViewModels;

namespace Noted.Services;

internal sealed record ChecklistReminder(Guid TaskId, DateTimeOffset ReminderAtUtc, string Text, DateTimeOffset BatchUtc = default,
    ChecklistNotificationStyle Style = ChecklistNotificationStyle.Standard,
    ChecklistReminderOccurrence? Occurrence = null, string Notes = "", ChecklistNotificationOptions? Options = null,
    ChecklistRepeatDelivery? RepeatDelivery = null);

internal readonly record struct ChecklistRepeatDelivery(Guid SeriesId, DateTimeOffset SlotUtc);

internal sealed class ChecklistReminderScheduler : IDisposable
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_clockCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_repeatPace = TimeSpan.FromSeconds(5);
    private readonly ChecklistWindowViewModel _model;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<ChecklistReminder>, bool> _submit;
    private readonly TimeProvider _clock;
    private readonly Func<ChecklistNotificationOptions>? _notificationDefaults;
    private readonly Func<IReadOnlyList<ChecklistReminder>, bool>? _submitUrgent;
    private readonly Dictionary<Guid, Guid> _presentedUrgent = [];
    private readonly Dictionary<Guid, ChecklistReminderActivation> _recoveredRepeatToasts;
    private readonly HashSet<Guid> _publishedRepeatSeries;
    private readonly Action<Guid>? _removeRepeat;
    private readonly Func<IReadOnlyList<ChecklistReminder>, Func<bool>, Task<bool>>? _submitPersistentRepeat;
    private long? _lastRepeatSubmission;
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
        Func<IReadOnlyList<ChecklistReminder>, bool>? submitUrgent = null,
        Func<ChecklistNotificationOptions>? notificationDefaults = null,
        IEnumerable<ChecklistReminderActivation>? repeatHistory = null, Action<Guid>? removeRepeat = null,
        Func<IReadOnlyList<ChecklistReminder>, Func<bool>, Task<bool>>? submitPersistentRepeat = null)
    {
        _model = model;
        _dispatcher = dispatcher;
        _submit = submit;
        _clock = clock ?? TimeProvider.System;
        _submitUrgent = submitUrgent;
        _notificationDefaults = notificationDefaults;
        _removeRepeat = removeRepeat;
        _submitPersistentRepeat = submitPersistentRepeat;
        _recoveredRepeatToasts = (repeatHistory ?? []).Where(activation => activation.RepeatDelivery.HasValue)
            .GroupBy(activation => activation.RepeatDelivery!.Value.SeriesId).ToDictionary(group => group.Key, group => group.First());
        _publishedRepeatSeries = _recoveredRepeatToasts.Keys.ToHashSet();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Timer_Tick;
        _model.ReminderStateChanged += ReminderStateChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemEvents.TimeChanged += TimeChanged;
        CheckNow();
    }

    internal string? LastError { get; private set; }

    internal void CheckNow() => _ = CheckNowAsync();

    private async Task CheckNowAsync()
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
            foreach (var taskId in _presentedUrgent.Keys.Except(_model.Items
                .Where(item => item.NotificationStyle == ChecklistNotificationStyle.Urgent &&
                    item.ReminderOccurrence is { AcknowledgedAtUtc: null } occurrence &&
                    _presentedUrgent.GetValueOrDefault(item.Id) == occurrence.Id)
                .Select(item => item.Id)).ToArray())
                _presentedUrgent.Remove(taskId);

            var canceled = FindCanceledRepeatToasts();
            if (canceled.Length == 0 && !_model.Items.Any(item => IsDue(item, now)))
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
            foreach (var seriesId in canceled)
            {
                _removeRepeat?.Invoke(seriesId);
                _publishedRepeatSeries.Remove(seriesId);
                _recoveredRepeatToasts.Remove(seriesId);
            }
            now = _clock.GetUtcNow();
            // the persisted submission time also identifies a grouped notification after restart.
            var batchUtc = now;
            var defaults = _notificationDefaults?.Invoke() ?? new ChecklistNotificationOptions();
            var reminders = _model.Items.Where(item => IsDue(item, now))
                .OrderBy(item => item.ReminderRepeat?.NextSlotUtc ?? item.ReminderAtUtc)
                .Select(item => new ChecklistReminder(item.Id, item.ReminderAtUtc!.Value, item.Text, batchUtc,
                    item.NotificationStyle, item.ReminderOccurrence, item.Notes,
                    (item.ReminderOccurrence?.Options ?? item.NotificationOptions ?? defaults).Normalize(), GetRepeatDelivery(item)))
                .ToArray();
            if (reminders.Length == 0) return;

            var failed = false;
            foreach (var group in reminders.GroupBy(reminder => (reminder.Style,
                RepeatTask: reminder.Style != ChecklistNotificationStyle.Urgent && reminder.RepeatDelivery.HasValue ? reminder.TaskId : (Guid?)null)))
            {
                if (_disposed || _suspended) return;
                // grouped activation must identify only this style's accepted delivery.
                while (_model.Items.Any(item => item.ReminderHandledAtUtc == batchUtc))
                    batchUtc = batchUtc.AddTicks(1);
                // an earlier delivery callback may cancel or replace another style's occurrence.
                var currentItems = _model.Items.ToDictionary(item => item.Id);
                var request = group.Where(reminder => currentItems.TryGetValue(reminder.TaskId, out var item) && IsCurrentRequest(item, reminder))
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
                    if (group.Key.Style == ChecklistNotificationStyle.Urgent)
                    {
                        var created = false;
                        for (var index = 0; index < request.Length; index++)
                        {
                            var reminder = request[index];
                            var item = currentItems[reminder.TaskId];
                            if (reminder.RepeatDelivery is { } repeat && item.ReminderOccurrence?.ScheduledAtUtc != repeat.SlotUtc)
                            {
                                var occurrence = ChecklistReminderOccurrence.Create(repeat.SlotUtc, item.ReminderOccurrence?.ConfirmationCode);
                                item.ReminderOccurrence = occurrence;
                                created = true;
                                request[index] = reminder with
                                {
                                    Occurrence = occurrence,
                                    Options = (item.NotificationOptions ?? defaults).Normalize()
                                };
                            }
                        }
                        if (created && !_model.TrySaveItems(null, out error))
                        {
                            Fail(error);
                            failed = true;
                            continue;
                        }
                        submitted = _submitUrgent?.Invoke(request) == true;
                    }
                    else if (request[0].RepeatDelivery is { } delivery)
                    {
                        if (IsRecovered(request[0].TaskId, delivery)) submitted = true;
                        else
                        {
                            _lastRepeatSubmission = _clock.GetTimestamp();
                            if (group.Key.Style == ChecklistNotificationStyle.Persistent && _submitPersistentRepeat is not null)
                            {
                                bool CanSubmit() => !_disposed && !_suspended && _model.Items.Any(item =>
                                    item.Id == request[0].TaskId && MatchesRequest(item, request[0]) && delivery.SlotUtc <= _clock.GetUtcNow());
                                submitted = await _submitPersistentRepeat(request, CanSubmit);
                                if (!submitted && !CanSubmit()) continue;
                            }
                            else submitted = _submit(request);
                        }
                        if (submitted)
                        {
                            _publishedRepeatSeries.Add(delivery.SeriesId);
                            _recoveredRepeatToasts.Remove(delivery.SeriesId);
                        }
                    }
                    else submitted = _submit(request);
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
                    if (reminder.Style == ChecklistNotificationStyle.Urgent && reminder.Occurrence is { } occurrence)
                        _presentedUrgent[reminder.TaskId] = occurrence.Id;
                if (!_model.TryMarkRemindersHandled(request, batchUtc, out error, acceptedAt: _clock.GetUtcNow()))
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
        catch (Exception exception)
        {
            ExceptionDiagnostics.Record(exception, "Check Checklist reminders");
            Fail("Checklist reminders could not be checked.");
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

    private bool IsDue(ChecklistItem item, DateTimeOffset now) => PendingAtUtc(item, now) <= now;

    private DateTimeOffset? PendingAtUtc(ChecklistItem item, DateTimeOffset now)
    {
        if (item.IsChecked || item.ReminderAtUtc is not { } scheduled) return null;
        if (item.NotificationStyle == ChecklistNotificationStyle.Urgent)
        {
            if (item.ReminderOccurrence is { AcknowledgedAtUtc: null } occurrence &&
                (item.ReminderRepeat is null || item.ReminderRepeat.LastSlotUtc == occurrence.ScheduledAtUtc))
            {
                if (_presentedUrgent.GetValueOrDefault(item.Id) == occurrence.Id) return null;
                // restore unresolved alerts even if the clock moved behind their original schedule.
                return item.ReminderHandledAtUtc is not null ? now : scheduled;
            }
            return GetRepeatDelivery(item)?.SlotUtc;
        }
        if (item.ReminderRepeat is not null)
        {
            if (GetRepeatDelivery(item) is not { } delivery) return null;
            if (IsRecovered(item.Id, delivery)) return now;
            var remaining = _lastRepeatSubmission is { } last ? s_repeatPace - _clock.GetElapsedTime(last) : TimeSpan.Zero;
            return remaining > TimeSpan.Zero && delivery.SlotUtc - now < remaining ? now + remaining : delivery.SlotUtc;
        }
        return item.ReminderHandledAtUtc is null ? scheduled : null;
    }

    private bool IsCurrentRequest(ChecklistItem item, ChecklistReminder reminder) =>
        MatchesRequest(item, reminder) && IsDue(item, _clock.GetUtcNow());

    private static bool MatchesRequest(ChecklistItem item, ChecklistReminder reminder) =>
        !item.IsChecked && item.ReminderAtUtc == reminder.ReminderAtUtc && item.NotificationStyle == reminder.Style &&
        item.ReminderOccurrence?.Id == reminder.Occurrence?.Id && GetRepeatDelivery(item) == reminder.RepeatDelivery;

    private static ChecklistRepeatDelivery? GetRepeatDelivery(ChecklistItem item)
    {
        if (item.ReminderRepeat is not { NextSlotUtc: { } slot } repeat) return null;
        if (item.NotificationStyle == ChecklistNotificationStyle.Urgent &&
            item.ReminderOccurrence is { AcknowledgedAtUtc: null } occurrence &&
            occurrence.ScheduledAtUtc == repeat.LastSlotUtc) return null;
        return new(repeat.Id, slot);
    }

    private bool IsRecovered(Guid taskId, ChecklistRepeatDelivery delivery) =>
        _recoveredRepeatToasts.TryGetValue(delivery.SeriesId, out var toast) &&
        toast.TaskId == taskId && toast.RepeatDelivery == delivery;

    private Guid[] FindCanceledRepeatToasts()
    {
        if (_publishedRepeatSeries.Count == 0) return [];
        var current = _model.Items.Where(item => item.NotificationStyle != ChecklistNotificationStyle.Urgent && item.ReminderRepeat is not null)
            .Select(item => item.ReminderRepeat!.Id).ToHashSet();
        return _publishedRepeatSeries.Where(id => !current.Contains(id)).ToArray();
    }

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
        var now = _clock.GetUtcNow();
        var next = FindCanceledRepeatToasts().Length > 0 ? now : _model.Items.Select(item => PendingAtUtc(item, now)).Min();
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
