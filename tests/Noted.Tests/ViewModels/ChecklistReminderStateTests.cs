using System.IO;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class ChecklistReminderStateTests
{
    [TestMethod]
    public void HistoricalItemsReceiveStableIdsWithoutChangingTheirContent() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        const string historical = """
            [{"Text":"old task","IsChecked":false,"Priority":2,"DueDate":"2026-09-15T00:00:00","Notes":"old notes","CreatedAt":"2026-09-01T10:30:00"}]
            """;
        File.WriteAllText(directory.File("checklist.json"), historical);
        Guid id;
        using (var model = Create(new ChecklistContentService(directory.Path)))
        {
            var item = model.Items.Single();
            id = item.Id;
            Assert.AreNotEqual(Guid.Empty, id);
            Assert.IsNull(item.DueTime);
            Assert.IsNull(item.ReminderAtUtc);
            Assert.IsNull(item.ReminderHandledAtUtc);
            Assert.AreEqual("old task", item.Text);
            Assert.AreEqual("old notes", item.Notes);
            Assert.AreEqual(ChecklistPriority.Medium, item.Priority);
            Assert.AreEqual(new DateTime(2026, 9, 15), item.DueDate);
            Assert.AreEqual(new DateTime(2026, 9, 1, 10, 30, 0), item.CreatedAt);
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        }
        using var reopened = Create(new ChecklistContentService(directory.Path));
        Assert.AreEqual(id, reopened.Items.Single().Id);
        Assert.AreEqual(historical, File.ReadAllText(directory.File("checklist.json.bak")));
    });

    [TestMethod]
    public void DuplicateIdsKeepAllTasksAndDisableRemindersOnReassignedItems() => WpfTestHost.Invoke(() =>
    {
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var reminder = DateTimeOffset.UtcNow.AddDays(1);
        var service = new RecordingService
        {
            Items = [new() { Id = id, Text = "first", ReminderAtUtc = reminder },
                new() { Id = id, Text = "second", ReminderAtUtc = reminder, ReminderHandledAtUtc = reminder },
                new() { Id = otherId, Text = "third" }, new() { Text = "missing" }]
        };
        using var model = Create(service);
        Assert.HasCount(4, model.Items.Select(item => item.Id).Distinct().ToList());
        Assert.IsFalse(model.Items.Any(item => item.Id == Guid.Empty));
        Assert.AreEqual(id, model.Items[0].Id);
        Assert.AreEqual(otherId, model.Items[2].Id);
        Assert.AreEqual(reminder, model.Items[0].ReminderAtUtc);
        Assert.IsNull(model.Items[1].ReminderAtUtc);
        Assert.IsNull(model.Items[1].ReminderHandledAtUtc);
        CollectionAssert.AreEqual(new[] { "first", "second", "third", "missing" }, model.Items.Select(item => item.Text).ToArray());
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        CollectionAssert.AreEqual(model.Items.Select(item => item.Id).ToArray(), service.Items.Select(item => item.Id).ToArray());
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompletingAndReopeningTasksDoesNotRestoreReminders(bool bulk) => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService();
        using var model = Create(service);
        var item = model.AddItem()!;
        item.ReminderAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        item.ReminderHandledAtUtc = DateTimeOffset.UtcNow;
        if (bulk) model.CheckAll(); else item.IsChecked = true;
        Assert.IsNull(item.ReminderAtUtc);
        Assert.IsNull(item.ReminderHandledAtUtc);
        if (bulk) model.UncheckAll(); else item.IsChecked = false;
        Assert.IsNull(item.ReminderAtUtc);
        Assert.IsNull(item.ReminderHandledAtUtc);
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.IsNull(service.Items.Single().ReminderAtUtc);
        Assert.IsNull(service.Items.Single().ReminderHandledAtUtc);
    });

    [TestMethod]
    public void DuplicateCreatesNewIdentityWithoutReminderAndMoveKeepsOriginalReminder() => WpfTestHost.Invoke(() =>
    {
        using var model = Create(new RecordingService());
        var item = model.AddItem()!;
        item.Text = "task";
        item.Notes = "notes";
        item.DueDate = new DateTime(2026, 12, 1);
        item.ReminderAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        item.ReminderHandledAtUtc = DateTimeOffset.UtcNow;
        model.DuplicateItem(item);
        var duplicate = model.Items[1];
        Assert.AreNotEqual(item.Id, duplicate.Id);
        Assert.AreEqual(item.Text, duplicate.Text);
        Assert.AreEqual(item.Notes, duplicate.Notes);
        Assert.AreEqual(item.DueDate, duplicate.DueDate);
        Assert.IsNull(duplicate.ReminderAtUtc);
        Assert.IsNull(duplicate.ReminderHandledAtUtc);
        model.MoveItemDown(item);
        Assert.IsNotNull(item.ReminderAtUtc);
        Assert.IsNotNull(item.ReminderHandledAtUtc);
        model.RemoveItem(item);
        Assert.IsNull(item.ReminderAtUtc);
        Assert.IsNull(item.ReminderHandledAtUtc);
        Assert.IsTrue(model.TryFlushPendingItems(out _));
    });

    [TestMethod]
    public void ReminderTimesRoundTripInUtcAndReschedulingClearsHandledState() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        Guid id;
        var local = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.FromHours(-8));
        using (var model = Create(new ChecklistContentService(directory.Path)))
        {
            var item = model.AddItem()!;
            id = item.Id;
            item.ReminderAtUtc = local;
            item.ReminderHandledAtUtc = local.AddMinutes(1);
            Assert.AreEqual(TimeSpan.Zero, item.ReminderAtUtc.Value.Offset);
            Assert.IsTrue(model.TryFlushPendingItems(out _));
        }
        using var reopened = Create(new ChecklistContentService(directory.Path));
        var restored = reopened.Items.Single();
        Assert.AreEqual(id, restored.Id);
        Assert.AreEqual(local.ToUniversalTime(), restored.ReminderAtUtc);
        Assert.AreEqual(local.AddMinutes(1).ToUniversalTime(), restored.ReminderHandledAtUtc);
        restored.ReminderAtUtc = local.AddDays(1);
        Assert.IsNull(restored.ReminderHandledAtUtc);
    });

    [TestMethod]
    public void FailedIdentitySaveRetainsAssignedIdsForRetry() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService { Items = [new() { Text = "legacy" }], FailSave = true };
        using var model = Create(service);
        var id = model.Items[0].Id;
        Assert.IsFalse(model.TryFlushPendingItems(out _));
        Assert.IsTrue(model.HasPersistenceError);
        service.FailSave = false;
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.AreEqual(id, service.Items[0].Id);
    });

    [TestMethod]
    public void CompletedItemsCannotRecoverAnOldReminderOnLoad() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService
        {
            Items = [new() { Id = Guid.NewGuid(), IsChecked = true,
                ReminderAtUtc = DateTimeOffset.UtcNow, ReminderHandledAtUtc = DateTimeOffset.UtcNow }]
        };
        using var model = Create(service);
        model.Items[0].IsChecked = false;
        Assert.IsNull(model.Items[0].ReminderAtUtc);
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.IsNull(service.Items[0].ReminderAtUtc);
    });

    [TestMethod]
    public void OptionalDueTimeSurvivesSaveReopenDuplicationAndBackupRecovery() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ChecklistContentService(directory.Path);
        service.SaveItems([new() { Text = "timed", DueDate = new DateTime(2027, 1, 2), DueTime = TimeSpan.Zero }]);
        using (var model = Create(service))
        {
            var item = model.Items.Single();
            Assert.AreEqual(TimeSpan.Zero, item.DueTime);
            model.DuplicateItem(item);
            Assert.AreEqual(TimeSpan.Zero, model.Items[1].DueTime);
            item.DueTime = new TimeSpan(14, 35, 0);
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        }
        using (var model = Create(new ChecklistContentService(directory.Path)))
        {
            Assert.AreEqual(new TimeSpan(14, 35, 0), model.Items[0].DueTime);
            model.Items[0].DueDate = null;
            Assert.IsNull(model.Items[0].DueTime);
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        }
        File.WriteAllText(directory.File("checklist.json"), "invalid json");
        using var recovered = Create(new ChecklistContentService(directory.Path));
        Assert.AreEqual(new TimeSpan(14, 35, 0), recovered.Items[0].DueTime);
        Assert.AreEqual(TimeSpan.Zero, recovered.Items[1].DueTime);
    });

    [TestMethod]
    public void DueTimeRejectsInvalidValuesAndRequiresADate()
    {
        var item = new ChecklistItem { DueTime = TimeSpan.FromHours(9) };
        Assert.IsNull(item.DueTime);
        item.DueDate = new DateTime(2027, 1, 2);
        item.DueTime = TimeSpan.FromDays(1);
        Assert.IsNull(item.DueTime);
        item.DueTime = TimeSpan.FromMinutes(-1);
        Assert.IsNull(item.DueTime);
        item.DueTime = TimeSpan.Zero;
        Assert.AreEqual(TimeSpan.Zero, item.DueTime);
    }

    [TestMethod]
    public void LinkedScheduleRoundTripsFollowsEditsAndDoesNotRearmAnUnchangedOccurrence() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService { Items = [new() { Text = "linked" }] };
        var due = DateTime.Today.AddDays(2);
        var time = TimeSpan.FromHours(14);
        var reminder = new DateTimeOffset(due.Add(time)).ToUniversalTime();
        Guid occurrence;
        using (var model = Create(service))
        {
            var item = model.Items.Single();
            Assert.IsTrue(model.TrySetSchedule(item, due, time, reminder, true, ChecklistNotificationStyle.Urgent, null, out var error), error);
            item.ReminderHandledAtUtc = DateTimeOffset.UtcNow;
            occurrence = item.ReminderOccurrence!.Id;
            Assert.IsTrue(model.TrySetSchedule(item, due, time, reminder, true, item.NotificationStyle, null, out error), error);
            Assert.AreEqual(occurrence, item.ReminderOccurrence.Id);
            Assert.IsNotNull(item.ReminderHandledAtUtc);
        }
        using var reopened = Create(service);
        var restored = reopened.Items.Single();
        Assert.IsTrue(restored.ReminderAtDueTime);
        Assert.AreEqual(occurrence, restored.ReminderOccurrence!.Id);
        Assert.IsTrue(reopened.TrySetSchedule(restored, due.AddDays(1), time, reminder.AddDays(1), true,
            restored.NotificationStyle, null, out var saveError), saveError);
        Assert.AreNotEqual(occurrence, restored.ReminderOccurrence!.Id);
        Assert.IsNull(restored.ReminderHandledAtUtc);
        reopened.DuplicateItem(restored);
        Assert.IsFalse(reopened.Items[1].ReminderAtDueTime);
        restored.IsChecked = true;
        Assert.IsFalse(restored.ReminderAtDueTime);
        Assert.IsNull(restored.ReminderAtUtc);
    });

    [TestMethod]
    public void CustomizationPreservesDeliveredUrgentStateAndStyleRequiresRescheduling() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService();
        using var model = Create(service);
        var item = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
        model.Items.Add(item);
        var handled = DateTimeOffset.UtcNow.AddMinutes(-9);
        item.ReminderHandledAtUtc = handled;
        item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new(), SilencedAtUtc = handled, AcknowledgedAtUtc = handled };
        var occurrence = item.ReminderOccurrence;
        Assert.IsTrue(model.TrySetSchedule(item, DateTime.Today.AddDays(2), null, item.ReminderAtUtc, false,
            item.NotificationStyle, new() { UrgentSound = ChecklistUrgentSound.Silent }, out var error), error);
        Assert.AreEqual(occurrence, item.ReminderOccurrence);
        Assert.AreEqual(handled, item.ReminderHandledAtUtc);
        Assert.IsFalse(model.TrySetSchedule(item, item.DueDate, null, item.ReminderAtUtc, false,
            ChecklistNotificationStyle.Standard, item.NotificationOptions, out error));
        Assert.AreEqual(occurrence, item.ReminderOccurrence);
        Assert.AreEqual(ChecklistNotificationStyle.Urgent, item.NotificationStyle);
    });

    [TestMethod]
    public void PendingStyleChangesPreserveOccurrenceAcrossSaveAndReload() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService();
        Guid id;
        using (var model = Create(service))
        {
            var item = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddDays(2) };
            model.Items.Add(item);
            id = item.ReminderOccurrence!.Id;
            Assert.IsTrue(model.TrySetSchedule(item, null, null, item.ReminderAtUtc, false,
                ChecklistNotificationStyle.Standard, null, out var error), error);
            Assert.AreEqual(id, item.ReminderOccurrence!.Id);
        }
        using var restored = Create(service);
        var task = restored.Items.Single();
        Assert.AreEqual(id, task.ReminderOccurrence!.Id);
        Assert.IsTrue(restored.TrySetSchedule(task, null, null, task.ReminderAtUtc, false,
            ChecklistNotificationStyle.Urgent, null, out var saveError), saveError);
        Assert.AreEqual(id, task.ReminderOccurrence!.Id);
    });

    [TestMethod]
    public void ExpiredNewNotificationIsRejectedButExistingOverdueSaveIsAllowed() => WpfTestHost.Invoke(() =>
    {
        using var model = Create(new RecordingService());
        var item = new ChecklistItem(); model.Items.Add(item);
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.IsFalse(model.TrySetSchedule(item, null, null, past, false, item.NotificationStyle, null, out _));
        Assert.IsNull(item.ReminderAtUtc);
        item.ReminderAtUtc = past;
        item.ReminderHandledAtUtc = DateTimeOffset.UtcNow;
        Assert.IsTrue(model.TrySetSchedule(item, null, null, past, false, item.NotificationStyle, null, out var error), error);
        Assert.IsNotNull(item.ReminderHandledAtUtc);
    });

    [TestMethod]
    public void FailedScheduleApplyRestoresTheOccurrenceAndKeepsOtherPendingEdits() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService();
        using var model = Create(service);
        var item = model.AddItem()!;
        item.NotificationStyle = ChecklistNotificationStyle.Urgent;
        item.ReminderAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
        item.ReminderHandledAtUtc = DateTimeOffset.UtcNow;
        item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new(), SilencedAtUtc = DateTimeOffset.UtcNow };
        var original = item.ReminderOccurrence;
        var handled = item.ReminderHandledAtUtc;
        Assert.IsTrue(model.TryFlushPendingItems(out _));

        item.Notes = "new notes";
        service.FailSave = true;
        Assert.IsFalse(model.TrySetSchedule(item, DateTime.Today.AddDays(1), null, DateTimeOffset.UtcNow.AddDays(1),
            false, ChecklistNotificationStyle.Standard, null, out _));
        Assert.IsNull(item.DueDate);
        Assert.AreEqual(ChecklistNotificationStyle.Urgent, item.NotificationStyle);
        Assert.AreEqual(original.ScheduledAtUtc, item.ReminderAtUtc);
        Assert.AreEqual(original, item.ReminderOccurrence);
        Assert.AreEqual(handled, item.ReminderHandledAtUtc);

        service.FailSave = false;
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.AreEqual("new notes", service.Items.Single().Notes);
        Assert.AreEqual(original, service.Items.Single().ReminderOccurrence);
    });

    [TestMethod]
    public void DueStatusRefreshDoesNotSaveOrChangeReminderState() => WpfTestHost.Invoke(() =>
    {
        var service = new RecordingService(); using var model = Create(service);
        var item = new ChecklistItem { DueDate = DateTime.Today }; model.Items.Add(item);
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        var saved = service.Items;
        var names = new List<string?>();
        item.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        item.RefreshDueStatus();
        Assert.AreSame(saved, service.Items);
        CollectionAssert.AreEqual(new[] { nameof(ChecklistItem.DueAtLocal) }, names.ToArray());
    });

    [TestMethod]
    public void CapturedOptionsKeepStyleStableEvenWhenSubmissionHasNotBeenRecorded() => WpfTestHost.Invoke(() =>
    {
        using var model = Create(new RecordingService());
        var item = new ChecklistItem { NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = DateTimeOffset.UtcNow.AddDays(2) };
        model.Items.Add(item);
        item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new() };
        var captured = item.ReminderOccurrence;
        Assert.IsFalse(model.TrySetSchedule(item, null, null, item.ReminderAtUtc, false,
            ChecklistNotificationStyle.Standard, null, out _));
        Assert.AreEqual(captured, item.ReminderOccurrence);
        Assert.IsNull(item.ReminderHandledAtUtc);
    });

    private static ChecklistWindowViewModel Create(IChecklistContentService service) =>
        new(service, Dispatcher.CurrentDispatcher, action => action(), false);

    private sealed class RecordingService : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; set; } = [];
        internal bool FailSave { get; set; }
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            if (FailSave) throw new IOException("save failed");
            Items = items.ToList();
            return new(null);
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
