using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistReminderPreferenceTests
{
    [TestMethod]
    public void PendingStyleChangesKeepOccurrenceIdentityAcrossRestart() => WpfTestHost.Invoke(() =>
    {
        var store = new Store();
        Guid occurrence;
        using (var model = Create(store))
        {
            var task = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddDays(2) };
            model.Items.Add(task);
            occurrence = task.ReminderOccurrence!.Id;
            Assert.IsTrue(model.TrySetReminder(task, task.ReminderAtUtc, ChecklistNotificationStyle.Standard, null, out var error), error);
            Assert.AreEqual(occurrence, task.ReminderOccurrence!.Id);
        }
        using var restored = Create(store);
        var item = restored.Items.Single();
        Assert.AreEqual(occurrence, item.ReminderOccurrence!.Id);
        Assert.IsTrue(restored.TrySetReminder(item, item.ReminderAtUtc, ChecklistNotificationStyle.Urgent, null, out var saveError), saveError);
        Assert.AreEqual(occurrence, item.ReminderOccurrence!.Id);
    });

    [TestMethod]
    public void PreferenceEditsPreserveDeliveredStateButTimingChangesReplaceIt() => WpfTestHost.Invoke(() =>
    {
        using var model = Create(new Store());
        var task = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
        model.Items.Add(task);
        var handled = DateTimeOffset.UtcNow;
        task.ReminderHandledAtUtc = handled;
        task.ReminderOccurrence = task.ReminderOccurrence! with { Options = new(), SilencedAtUtc = handled, AcknowledgedAtUtc = handled };
        var original = task.ReminderOccurrence;
        Assert.IsTrue(model.TrySetReminder(task, task.ReminderAtUtc, task.NotificationStyle,
            new() { RepeatSound = false }, out var error), error);
        Assert.AreEqual(original, task.ReminderOccurrence);
        Assert.AreEqual(handled, task.ReminderHandledAtUtc);
        Assert.IsFalse(model.TrySetReminder(task, task.ReminderAtUtc, ChecklistNotificationStyle.Standard, null, out _));
        Assert.AreEqual(original, task.ReminderOccurrence);
        Assert.IsTrue(model.TrySetReminder(task, DateTimeOffset.UtcNow.AddDays(1), out error), error);
        Assert.AreNotEqual(original!.Id, task.ReminderOccurrence!.Id);
        Assert.IsNull(task.ReminderOccurrence.Options);
        Assert.IsNull(task.ReminderOccurrence.AcknowledgedAtUtc);
        Assert.IsNull(task.ReminderOccurrence.SilencedAtUtc);
        Assert.IsNull(task.ReminderHandledAtUtc);
    });

    [TestMethod]
    public void CapturedOptionsProtectStyleBeforeSubmissionIsRecorded() => WpfTestHost.Invoke(() =>
    {
        using var model = Create(new Store());
        var task = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        model.Items.Add(task);
        task.ReminderOccurrence = task.ReminderOccurrence! with { Options = new() };
        Assert.IsFalse(model.TrySetReminder(task, task.ReminderAtUtc, ChecklistNotificationStyle.Standard, null, out _));
        Assert.AreEqual(ChecklistNotificationStyle.Urgent, task.NotificationStyle);
    });

    [TestMethod]
    public void FailedPreferenceSaveKeepsOccurrenceStateForRetry() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var model = Create(store);
        var task = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddDays(1) };
        model.Items.Add(task);
        var original = task.ReminderOccurrence;
        store.FailSave = true;
        Assert.IsFalse(model.TrySetReminder(task, task.ReminderAtUtc, task.NotificationStyle, new() { RepeatSound = false }, out _));
        Assert.AreEqual(original, task.ReminderOccurrence);
        store.FailSave = false;
        Assert.IsTrue(model.TrySetReminder(task, task.ReminderAtUtc, out var error), error);
        Assert.AreEqual(original, store.Items.Single().ReminderOccurrence);
    });

    private static ChecklistWindowViewModel Create(IChecklistContentService store) =>
        new(store, Dispatcher.CurrentDispatcher, action => action(), false);

    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; private set; } = [];
        internal bool FailSave { get; set; }
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            if (FailSave) throw new System.IO.IOException("save failed");
            Items = items.ToArray();
            return new(null);
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
