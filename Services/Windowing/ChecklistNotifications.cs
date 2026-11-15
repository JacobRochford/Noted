using System.Globalization;
using Noted.Models;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Noted.Services;

internal enum ChecklistWindowsNotificationSound
{
    Default,
    Silent,
    Reminder,
    RepeatingAlarm
}

internal sealed record ChecklistNotificationContent(string Title, string Text, IReadOnlyDictionary<string, string> Arguments,
    ChecklistNotificationStyle Style = ChecklistNotificationStyle.Standard,
    ChecklistWindowsNotificationSound Sound = ChecklistWindowsNotificationSound.Reminder)
{
    internal static ChecklistNotificationContent Create(IReadOnlyList<ChecklistReminder> reminders,
        ChecklistWindowsNotificationSound sound = ChecklistWindowsNotificationSound.Reminder)
    {
        if (reminders.Count == 0) throw new ArgumentException("A reminder is required.", nameof(reminders));
        var style = reminders[0].Style;
        if (style is not (ChecklistNotificationStyle.Standard or ChecklistNotificationStyle.Persistent) ||
            reminders.Any(reminder => reminder.Style != style))
            throw new ArgumentException("A Windows notification requires one supported reminder style.", nameof(reminders));
        if (style == ChecklistNotificationStyle.Persistent && !Enum.IsDefined(sound))
            throw new ArgumentOutOfRangeException(nameof(sound));
        var arguments = new Dictionary<string, string> { ["action"] = "checklistReminder" };
        if (reminders.Count == 1)
            arguments["taskId"] = reminders[0].TaskId.ToString("D");
        else
            arguments["batchUtc"] = reminders[0].BatchUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return new(reminders.Count == 1 ? "Checklist reminder" : $"{reminders.Count} Checklist reminders",
            reminders.Count == 1 ? Trim(reminders[0].Text) : "Open Checklist to choose a reminded task.", arguments, style, sound);
    }

    internal AppNotification BuildNotification()
    {
        var builder = new AppNotificationBuilder().AddText(Title).AddText(Text);
        foreach (var argument in Arguments) builder.AddArgument(argument.Key, argument.Value);
        if (Style == ChecklistNotificationStyle.Persistent)
        {
            builder.SetScenario(AppNotificationScenario.Reminder);
            var open = new AppNotificationButton(Arguments.ContainsKey("taskId") ? "Open task" : "Open Checklist");
            foreach (var argument in Arguments) open.AddArgument(argument.Key, argument.Value);
            builder.AddButton(open);
            switch (Sound)
            {
                case ChecklistWindowsNotificationSound.Default:
                    builder.SetAudioEvent(AppNotificationSoundEvent.Default);
                    break;
                case ChecklistWindowsNotificationSound.Silent:
                    builder.MuteAudio();
                    break;
                case ChecklistWindowsNotificationSound.Reminder:
                    builder.SetAudioEvent(AppNotificationSoundEvent.Reminder);
                    break;
                case ChecklistWindowsNotificationSound.RepeatingAlarm:
                    builder.SetDuration(AppNotificationDuration.Long);
                    builder.SetAudioEvent(AppNotificationSoundEvent.Alarm, AppNotificationAudioLooping.Loop);
                    break;
            }
        }
        return builder.BuildNotification();
    }

    private static string Trim(string text) => string.IsNullOrWhiteSpace(text) ? "Untitled task" :
        text.Length > 200 ? text[..200] + "…" : text;
}

internal sealed class ChecklistNotifications : IDisposable
{
    private readonly Action<ChecklistReminderActivation> _activate;
    private readonly Func<ChecklistNotificationContent, bool>? _testSubmit;
    private AppNotificationManager? _manager;

    internal ChecklistNotifications(Action<ChecklistReminderActivation> activate,
        Func<ChecklistNotificationContent, bool>? testSubmit = null)
    {
        _activate = activate;
        _testSubmit = testSubmit;
    }

    internal bool TryRegister()
    {
        if (_manager is not null) return true;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += NotificationInvoked;
            try { manager.Register(); }
            catch { manager.NotificationInvoked -= NotificationInvoked; throw; }
            _manager = manager;
            return true;
        }
        catch (Exception exception)
        {
            ExceptionDiagnostics.Record(exception, "Register Checklist notifications");
            return false;
        }
    }

    internal void ReadStartupActivation()
    {
        if (_manager is null) return;
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification)
            Activate((AppNotificationActivatedEventArgs)activation.Data);
    }

    internal bool TrySubmit(IReadOnlyList<ChecklistReminder> reminders,
        ChecklistWindowsNotificationSound sound = ChecklistWindowsNotificationSound.Reminder)
    {
        try
        {
            var content = ChecklistNotificationContent.Create(reminders, sound);
            if (_testSubmit is not null) return _testSubmit(content);
            if (!TryRegister() || _manager!.Setting != AppNotificationSetting.Enabled) return false;
            var notification = content.BuildNotification();
            _manager.Show(notification);
            return notification.Id != 0;
        }
        catch (Exception exception)
        {
            ExceptionDiagnostics.Record(exception, "Submit Checklist notification");
            return false;
        }
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs activation) => Activate(activation);

    private void Activate(AppNotificationActivatedEventArgs activation)
    {
        if (ChecklistReminderActivation.TryParse(activation.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value), out var request))
            _activate(request!);
    }

    public void Dispose()
    {
        if (_manager is null) return;
        _manager.NotificationInvoked -= NotificationInvoked;
        try { _manager.Unregister(); }
        catch (Exception exception) { ExceptionDiagnostics.Record(exception, "Unregister Checklist notifications"); }
        _manager = null;
    }
}
