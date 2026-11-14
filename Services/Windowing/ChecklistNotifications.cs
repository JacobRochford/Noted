using System.Globalization;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Noted.Services;

internal sealed record ChecklistNotificationContent(string Title, string Text, IReadOnlyDictionary<string, string> Arguments)
{
    internal static ChecklistNotificationContent Create(IReadOnlyList<ChecklistReminder> reminders)
    {
        if (reminders.Count == 0) throw new ArgumentException("A reminder is required.", nameof(reminders));
        var arguments = new Dictionary<string, string> { ["action"] = "checklistReminder" };
        if (reminders.Count == 1)
            arguments["taskId"] = reminders[0].TaskId.ToString("D");
        else
            arguments["batchUtc"] = reminders[0].BatchUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return new(reminders.Count == 1 ? "Checklist reminder" : $"{reminders.Count} Checklist reminders",
            reminders.Count == 1 ? Trim(reminders[0].Text) : "Open Checklist to choose a reminded task.", arguments);
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

    internal bool TrySubmit(IReadOnlyList<ChecklistReminder> reminders)
    {
        try
        {
            var content = ChecklistNotificationContent.Create(reminders);
            if (_testSubmit is not null) return _testSubmit(content);
            if (!TryRegister() || _manager!.Setting != AppNotificationSetting.Enabled) return false;
            var builder = new AppNotificationBuilder().AddText(content.Title).AddText(content.Text);
            foreach (var argument in content.Arguments) builder.AddArgument(argument.Key, argument.Value);
            var notification = builder.BuildNotification();
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
