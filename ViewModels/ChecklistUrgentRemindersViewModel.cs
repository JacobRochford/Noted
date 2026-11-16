using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public sealed class ChecklistUrgentReminderEntry : ObservableObject, IDisposable
{
    private readonly ChecklistWindowViewModel _owner;
    internal ChecklistUrgentReminderEntry(ChecklistWindowViewModel owner, ChecklistItem item)
    {
        _owner = owner;
        Item = item;
        OccurrenceId = item.ReminderOccurrence!.Id;
        ScheduledAtUtc = item.ReminderOccurrence.ScheduledAtUtc;
        item.PropertyChanged += ItemChanged;
    }
    public ChecklistItem Item { get; }
    public Guid OccurrenceId { get; }
    public DateTimeOffset ScheduledAtUtc { get; }
    public string Title => string.IsNullOrWhiteSpace(Item.Text) ? "Untitled task" : Item.Text;
    public ChecklistTab? Tab => _owner.Tabs.FirstOrDefault(tab => tab.Id == (Item.TabId ?? ChecklistTab.AllId));
    public string ScheduledText => $"Scheduled {ScheduledAtUtc.ToLocalTime():g}";
    public bool IsSilenced => Item.ReminderOccurrence?.SilencedAtUtc is not null;
    public ChecklistNotificationOptions Options => (Item.ReminderOccurrence?.Options ?? new()).Normalize();
    public bool UsesCode => Options.Acknowledgment == ChecklistAcknowledgment.Code;
    public bool UsesHold => Options.Acknowledgment == ChecklistAcknowledgment.Hold;
    public bool UsesButton => Options.Acknowledgment == ChecklistAcknowledgment.Button;
    public string ConfirmationPrompt => $"Enter code {Item.ReminderOccurrence?.ConfirmationCode} to acknowledge";
    private string _confirmationInput = "";
    public string ConfirmationInput { get => _confirmationInput; set => SetProperty(ref _confirmationInput, value); }
    internal long? HoldStarted { get; set; }
    private double _holdProgress;
    public double HoldProgress { get => _holdProgress; internal set => SetProperty(ref _holdProgress, value); }
    public string HoldText => $"Hold for {Options.HoldSeconds} seconds, then release";
    public string SilenceText => IsSilenced ? "Silenced" : "Silence";
    private string _overdueText = "";
    public string OverdueText { get => _overdueText; private set => SetProperty(ref _overdueText, value); }
    internal void RefreshTime(DateTimeOffset now)
    {
        var elapsed = now - ScheduledAtUtc;
        OverdueText = elapsed < TimeSpan.Zero ? "Previously delivered; clock is before the scheduled time" : elapsed.TotalDays >= 1
            ? $"Overdue by {(int)elapsed.TotalDays}d {elapsed.Hours}h"
            : elapsed.TotalHours >= 1 ? $"Overdue by {(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : elapsed.TotalMinutes >= 1 ? $"Overdue by {(int)elapsed.TotalMinutes}m" : "Due now";
        OnPropertyChanged(nameof(ScheduledText));
        OnPropertyChanged(nameof(Tab));
    }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChecklistItem.Text)) OnPropertyChanged(nameof(Title));
        if (e.PropertyName == nameof(ChecklistItem.TabId)) OnPropertyChanged(nameof(Tab));
        if (e.PropertyName == nameof(ChecklistItem.ReminderOccurrence))
        {
            OnPropertyChanged(nameof(Options));
            OnPropertyChanged(nameof(UsesCode)); OnPropertyChanged(nameof(UsesHold)); OnPropertyChanged(nameof(UsesButton));
            OnPropertyChanged(nameof(ConfirmationPrompt));
            OnPropertyChanged(nameof(IsSilenced));
            OnPropertyChanged(nameof(SilenceText));
        }
    }
    public void Dispose() => Item.PropertyChanged -= ItemChanged;
}

public sealed class ChecklistUrgentRemindersViewModel : ObservableObject, IDisposable
{
    private readonly ChecklistWindowViewModel _owner;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ObservableCollection<ChecklistUrgentReminderEntry> _entries = [];
    private bool _refreshQueued;
    private bool _updatingAction;
    private bool _disposed;
    private string? _statusMessage;

    internal ChecklistUrgentRemindersViewModel(ChecklistWindowViewModel owner, Dispatcher dispatcher, TimeProvider? clock = null, bool isPreview = false)
    {
        _owner = owner;
        IsPreview = isPreview;
        _dispatcher = dispatcher;
        _clock = clock ?? TimeProvider.System;
        Entries = new(_entries);
        owner.ReminderStateChanged += OwnerChanged;
    }
    public bool IsPreview { get; }
    public ReadOnlyObservableCollection<ChecklistUrgentReminderEntry> Entries { get; }
    public string Summary => Entries.Count == 1 ? "1 urgent reminder" : $"{Entries.Count} urgent reminders";
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    internal event EventHandler? Changed;
    internal event Action<Guid>? OpenTaskRequested;

    internal bool TryAccept(IReadOnlyList<ChecklistReminder> reminders)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || reminders.Count == 0) return false;
        var items = _owner.Items.ToDictionary(item => item.Id);
        if (reminders.Any(reminder => reminder.Style != ChecklistNotificationStyle.Urgent || reminder.Occurrence is null)) return false;
        foreach (var reminder in reminders)
        {
            if (!items.TryGetValue(reminder.TaskId, out var item) || item.IsChecked ||
                item.NotificationStyle != ChecklistNotificationStyle.Urgent || item.ReminderAtUtc != reminder.ReminderAtUtc ||
                (item.ReminderAtUtc > _clock.GetUtcNow() && item.ReminderHandledAtUtc is null) || item.ReminderOccurrence is not { AcknowledgedAtUtc: null } occurrence ||
                occurrence.Id != reminder.Occurrence!.Id) continue;
            if (!_entries.Any(entry => entry.Item.Id == item.Id && entry.OccurrenceId == occurrence.Id))
                _entries.Add(new(_owner, item));
        }
        Refresh();
        return _entries.Count > 0;
    }

    internal bool TryAcknowledge(ChecklistUrgentReminderEntry entry)
    {
        if (entry.UsesHold) { StatusMessage = "Hold the acknowledgment control, then release."; return false; }
        if (entry.UsesCode && !string.Equals(entry.ConfirmationInput, entry.Item.ReminderOccurrence?.ConfirmationCode, StringComparison.Ordinal))
        { StatusMessage = "Enter the four-digit code shown for this reminder."; return false; }
        return UpdateAction(entry, acknowledge: true);
    }
    internal void BeginHold(ChecklistUrgentReminderEntry entry)
    {
        CancelHolds();
        if (_entries.Contains(entry) && entry.UsesHold) entry.HoldStarted = _clock.GetTimestamp();
    }
    internal void UpdateHold(ChecklistUrgentReminderEntry entry)
    {
        if (entry.HoldStarted is { } started)
            entry.HoldProgress = Math.Clamp(_clock.GetElapsedTime(started).TotalSeconds / entry.Options.HoldSeconds, 0, 1);
    }
    internal bool FinishHold(ChecklistUrgentReminderEntry entry)
    {
        UpdateHold(entry);
        var ready = entry.HoldStarted.HasValue && entry.HoldProgress >= 1;
        CancelHolds();
        return ready && UpdateAction(entry, acknowledge: true);
    }
    internal void CancelHolds()
    {
        foreach (var entry in _entries) { entry.HoldStarted = null; entry.HoldProgress = 0; }
    }
    internal bool TrySilence(ChecklistUrgentReminderEntry entry) => UpdateAction(entry, acknowledge: false);
    private bool UpdateAction(ChecklistUrgentReminderEntry entry, bool acknowledge)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !_entries.Contains(entry) || _updatingAction) return false;
        _updatingAction = true;
        try
        {
            string? error;
            var saved = acknowledge
                ? _owner.TryAcknowledgeUrgentReminder(entry.Item.Id, entry.OccurrenceId, _clock.GetUtcNow(), out error)
                : _owner.TrySilenceUrgentReminder(entry.Item.Id, entry.OccurrenceId, _clock.GetUtcNow(), out error);
            StatusMessage = saved ? null : error ?? "The reminder could not be saved. Please retry.";
            return saved;
        }
        finally
        {
            _updatingAction = false;
            Refresh();
        }
    }
    internal void OpenTask(ChecklistUrgentReminderEntry entry)
    {
        _dispatcher.VerifyAccess();
        Refresh();
        if (!_disposed && _entries.Contains(entry)) OpenTaskRequested?.Invoke(entry.Item.Id);
    }
    internal void ShowMessage(string message) => StatusMessage = message;
    internal void Refresh()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _updatingAction) return;
        foreach (var entry in _entries.ToArray())
        {
            var item = entry.Item;
            if (!_owner.Items.Contains(item) || item.IsChecked || item.NotificationStyle != ChecklistNotificationStyle.Urgent ||
                item.ReminderOccurrence is not { AcknowledgedAtUtc: null } occurrence || occurrence.Id != entry.OccurrenceId)
            {
                _entries.Remove(entry);
                entry.Dispose();
            }
            else entry.RefreshTime(_clock.GetUtcNow());
        }
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OwnerChanged(object? sender, EventArgs e)
    {
        if (_disposed || _refreshQueued || _dispatcher.HasShutdownStarted) return;
        // reconcile after durable actions finish, including rollback after a failed write.
        _refreshQueued = true;
        _dispatcher.BeginInvoke(() => { _refreshQueued = false; Refresh(); });
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _owner.ReminderStateChanged -= OwnerChanged;
        foreach (var entry in _entries) entry.Dispose();
        _entries.Clear();
    }
}
