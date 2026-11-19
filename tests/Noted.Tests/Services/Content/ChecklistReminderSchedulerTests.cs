using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Reflection;
using Microsoft.Win32;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistReminderSchedulerTests
{
    [TestMethod]
    public void StartupGroupsMissedRemindersWithoutCreatingAWindow() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store
        {
            Items = [Due(clock, "one"), Due(clock, "two"),
                new() { Id = Guid.NewGuid(), Text = "future", ReminderAtUtc = clock.GetUtcNow().AddDays(1) },
                new() { Id = Guid.NewGuid(), Text = "handled", ReminderAtUtc = clock.GetUtcNow().AddDays(-2),
                    ReminderHandledAtUtc = clock.GetUtcNow().AddDays(-1) }]
        };
        var windows = Application.Current.Windows.OfType<ChecklistWindow>().Count();
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        Assert.AreEqual(1, service.Loads);
        Assert.AreEqual(windows, Application.Current.Windows.OfType<ChecklistWindow>().Count());
        Assert.HasCount(1, receiver.Requests);
        CollectionAssert.AreEqual(new[] { "one", "two" }, receiver.Requests[0].Select(item => item.Text).ToArray());
        Assert.IsTrue(service.Items.Take(2).All(item => item.ReminderHandledAtUtc == clock.GetUtcNow()));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
    });

    [TestMethod]
    public void TimerDeliversANewlyCreatedReminderWhenItBecomesDue() => WpfTestHost.Invoke(() =>
    {
        var service = new Store();
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit);
        Assert.AreEqual(0, service.Saves);
        var item = model.AddItem()!;
        item.Text = "timer task";
        item.ReminderAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(80);
        PumpUntil(() => receiver.Requests.Count == 1);
        Assert.AreEqual(item.Id, receiver.Requests[0].Single().TaskId);
        Assert.IsNotNull(item.ReminderHandledAtUtc);
        Assert.IsNotNull(service.Items.Single().ReminderHandledAtUtc);
    });

    [TestMethod]
    public void ClosingAndReopeningChecklistDoesNotInterruptDeliveryOrReloadData() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var clock = new Clock();
        var service = new Store();
        using var model = Model(service);
        var item = model.AddItem()!;
        item.ReminderAtUtc = clock.GetUtcNow().AddMinutes(1);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        var first = new ChecklistWindow(settings, model);
        first.Show();
        first.Close();
        clock.Advance(TimeSpan.FromMinutes(2));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        var reopened = new ChecklistWindow(settings, model);
        try
        {
            reopened.Show();
            Assert.AreSame(item, model.Items.Single());
            Assert.IsNotNull(item.ReminderHandledAtUtc);
            Assert.AreEqual(1, service.Loads);
            scheduler.CheckNow();
            Assert.HasCount(1, receiver.Requests);
        }
        finally { reopened.Close(); }
    });

    [TestMethod]
    public void CompletionDeletionAndBulkCompletionCancelPendingRequests() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        using var model = Model(new Store());
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        var completed = model.AddItem()!;
        var deleted = model.AddItem()!;
        var bulk = model.AddItem()!;
        foreach (var item in model.Items) item.ReminderAtUtc = clock.GetUtcNow().AddMinutes(1);
        completed.IsChecked = true;
        model.RemoveItem(deleted);
        model.CheckAll();
        model.UncheckAll();
        clock.Advance(TimeSpan.FromMinutes(2));
        scheduler.CheckNow();
        Assert.HasCount(0, receiver.Requests);
        Assert.IsNull(completed.ReminderAtUtc);
        Assert.IsNull(bulk.ReminderAtUtc);
    });

    [TestMethod]
    public void EditingAScheduleReplacesThePendingTimeAndClearsHandledState() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store { Items = [Due(clock)] };
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        var item = model.Items.Single();
        item.ReminderAtUtc = clock.GetUtcNow().AddHours(1);
        Assert.IsNull(item.ReminderHandledAtUtc);
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        clock.Advance(TimeSpan.FromHours(1));
        scheduler.CheckNow();
        Assert.HasCount(2, receiver.Requests);
        Assert.AreEqual(item.ReminderAtUtc, receiver.Requests[1].Single().ReminderAtUtc);
    });

    [TestMethod]
    public void FailedSubmissionsStayPendingAndUseMonotonicRetryDelay() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        using var model = Model(new Store { Items = [Due(clock)] });
        var receiver = new Receiver { Success = false };
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        Assert.IsNull(model.Items[0].ReminderHandledAtUtc);
        Assert.HasCount(1, receiver.Requests);
        clock.SetUtc(clock.GetUtcNow().AddDays(1));
        scheduler.CheckNow();
        clock.Advance(TimeSpan.FromSeconds(29));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        receiver.Success = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        scheduler.CheckNow();
        Assert.HasCount(2, receiver.Requests);
        Assert.IsNotNull(model.Items[0].ReminderHandledAtUtc);
        Assert.IsNull(scheduler.LastError);
    });

    [TestMethod]
    public void FailedScheduleSavePreventsSubmissionUntilPersistenceRecovers() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store { Items = [Due(clock)], FailSave = true };
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        Assert.HasCount(0, receiver.Requests);
        Assert.IsTrue(model.HasPersistenceError);
        service.FailSave = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        Assert.IsNotNull(service.Items[0].ReminderHandledAtUtc);
    });

    [TestMethod]
    public void FailedHandledSaveRetriesPersistenceWithoutSubmittingAgainAndSurvivesRestart() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store { Items = [Due(clock)] };
        var receiver = new Receiver();
        using (var model = Model(service))
        using (var scheduler = Scheduler(model, request =>
        {
            service.FailSave = true;
            return receiver.Submit(request);
        }, clock))
        {
            Assert.IsNotNull(model.Items[0].ReminderHandledAtUtc);
            Assert.IsNull(service.Items[0].ReminderHandledAtUtc);
            scheduler.CheckNow();
            Assert.HasCount(1, receiver.Requests);
            service.FailSave = false;
            clock.Advance(TimeSpan.FromSeconds(30));
            scheduler.CheckNow();
            Assert.IsNotNull(service.Items[0].ReminderHandledAtUtc);
        }
        using var restarted = Model(service);
        using var restartedScheduler = Scheduler(restarted, receiver.Submit, clock);
        Assert.HasCount(1, receiver.Requests);
    });

    [TestMethod]
    public void SubmissionExceptionsKeepReminderPending() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        using var model = Model(new Store { Items = [Due(clock)] });
        using var scheduler = Scheduler(model, _ => throw new IOException("receiver failed"), clock);
        Assert.IsNotNull(scheduler.LastError);
        Assert.IsNull(model.Items[0].ReminderHandledAtUtc);
    });

    [TestMethod]
    public void SubmissionDoesNotMarkRescheduledCompletedOrDeletedTasksHandled() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store { Items = [Due(clock), Due(clock), Due(clock)] };
        using var model = Model(service);
        var edited = model.Items[0];
        var completed = model.Items[1];
        var deleted = model.Items[2];
        using var scheduler = Scheduler(model, _ =>
        {
            edited.ReminderAtUtc = clock.GetUtcNow().AddDays(1);
            completed.IsChecked = true;
            model.RemoveItem(deleted);
            return true;
        }, clock);
        Assert.IsNull(edited.ReminderHandledAtUtc);
        Assert.IsNull(completed.ReminderHandledAtUtc);
        Assert.IsNull(deleted.ReminderHandledAtUtc);
        Assert.HasCount(2, service.Items);
        Assert.AreEqual(edited.ReminderAtUtc, service.Items[0].ReminderAtUtc);
    });

    [TestMethod]
    public void ResumeCheckGroupsMissedTimesAndShutdownSuspensionBlocksDelivery() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store { Items = [Due(clock), Due(clock)] };
        service.Items = service.Items.Select(item => item with { ReminderAtUtc = clock.GetUtcNow().AddMinutes(1) }).ToArray();
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        scheduler.SetSuspended(true);
        clock.Advance(TimeSpan.FromHours(2));
        scheduler.CheckNow();
        Assert.HasCount(0, receiver.Requests);
        scheduler.SetSuspended(false);
        Assert.HasCount(1, receiver.Requests);
        Assert.HasCount(2, receiver.Requests[0]);
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        scheduler.Dispose();
        model.Items[0].ReminderAtUtc = clock.GetUtcNow();
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
    });

    [TestMethod]
    public void ClockChangesUseCurrentUtcRatherThanAnElapsedCountdown() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store { Items = [Due(clock)] };
        service.Items = [service.Items[0] with { ReminderAtUtc = clock.GetUtcNow().AddHours(1) }];
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        var original = clock.GetUtcNow();
        clock.SetUtc(original.AddDays(-1));
        scheduler.CheckNow();
        Assert.HasCount(0, receiver.Requests);
        clock.SetUtc(original.AddHours(2));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
    });

    [TestMethod]
    public void ResumeEventQueuesCatchUpOnTheOwningDispatcher() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var service = new Store
        {
            Items = [Due(clock) with { ReminderAtUtc = clock.GetUtcNow().AddMinutes(1) },
                Due(clock) with { ReminderAtUtc = clock.GetUtcNow().AddMinutes(2) }]
        };
        using var model = Model(service);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        clock.Advance(TimeSpan.FromHours(2));
        var handler = typeof(ChecklistReminderScheduler).GetMethod("PowerModeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task.Run(() => handler.Invoke(scheduler, [new object(), new PowerModeChangedEventArgs(PowerModes.Resume)]))
            .GetAwaiter().GetResult();
        PumpUntil(() => receiver.Requests.Count == 1);
        Assert.HasCount(2, receiver.Requests[0]);
    });

    [TestMethod]
    public void RecoveredBackupRemindersAreGroupedAndHandledThroughTheExistingStore() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        File.WriteAllText(directory.File("checklist.json"), "{broken");
        File.WriteAllText(directory.File("checklist.json.bak"), """
            [{"Id":"7832106e-e340-4b11-a8fa-f61d26605ce8","Text":"one","ReminderAtUtc":"2026-12-01T17:00:00Z"},
             {"Id":"1ed25cba-0237-4105-bf2a-35a7d00d158f","Text":"two","ReminderAtUtc":"2026-12-02T17:00:00Z"}]
            """);
        using var model = Model(new ChecklistContentService(directory.Path));
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, new Clock());
        Assert.IsTrue(model.RecoveryIssuesFoundThisRun);
        Assert.HasCount(1, receiver.Requests);
        Assert.HasCount(2, receiver.Requests[0]);
        var saved = new ChecklistContentService(directory.Path).LoadItems().Items;
        Assert.IsTrue(saved.All(item => item.ReminderHandledAtUtc is not null));
    });

    [TestMethod]
    public void RepeatLimitCountsAdditionalAcceptedAlertsAndLeavesTheLastToastCurrent() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var saved = Repeating(clock, 10, 2);
        var start = clock.GetUtcNow();
        var store = new Store { Items = [saved] };
        using var model = Model(store);
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        var task = model.Items.Single();
        var first = receiver.Requests.Single().Single();
        Assert.AreEqual(0L, task.ReminderRepeat!.AdditionalAlertsSent);
        Assert.AreEqual(start.AddMinutes(10), task.ReminderRepeat.NextSlotUtc);

        clock.Advance(TimeSpan.FromMinutes(10));
        receiver.Success = false;
        scheduler.CheckNow();
        Assert.AreEqual(0L, task.ReminderRepeat.AdditionalAlertsSent);
        receiver.Success = true;
        clock.Advance(TimeSpan.FromSeconds(31));
        scheduler.CheckNow();
        Assert.AreEqual(1L, task.ReminderRepeat.AdditionalAlertsSent);
        Assert.AreEqual(start.AddMinutes(30), task.ReminderRepeat.NextSlotUtc);
        Assert.IsFalse(model.IsCurrentRepeatNotification(new(task.Id, null, first.RepeatDelivery)));

        clock.Advance(TimeSpan.FromMinutes(20));
        scheduler.CheckNow();
        var last = receiver.Requests.Last().Single();
        Assert.AreEqual(2L, task.ReminderRepeat.AdditionalAlertsSent);
        Assert.IsNull(task.ReminderRepeat.NextSlotUtc);
        Assert.IsFalse(task.HasActiveReminder);
        Assert.IsTrue(model.IsCurrentRepeatNotification(new(task.Id, null, last.RepeatDelivery)));
        Assert.AreEqual(task.ReminderRepeat, store.Items.Single().ReminderRepeat);
        clock.Advance(TimeSpan.FromHours(3));
        scheduler.CheckNow();
        Assert.HasCount(4, receiver.Requests);
    });

    [TestMethod]
    public void RecoveryCoalescesMissedSlotsAndPacesDifferentTasks() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var first = Repeating(clock, 10, 3);
        var second = Repeating(clock, 10, 3);
        var store = new Store { Items = [first, second] };
        using var model = Model(store);
        var receiver = new Receiver();
        var start = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(37));
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        Assert.AreEqual(start.AddMinutes(50), model.Items[0].ReminderRepeat!.NextSlotUtc);
        Assert.AreEqual(0L, model.Items[0].ReminderRepeat!.AdditionalAlertsSent);
        clock.Advance(TimeSpan.FromSeconds(5));
        scheduler.CheckNow();
        Assert.HasCount(2, receiver.Requests);
        Assert.AreEqual(second.Id, receiver.Requests[1].Single().TaskId);

        clock.Advance(TimeSpan.FromMinutes(13) - TimeSpan.FromSeconds(5));
        scheduler.CheckNow();
        Assert.AreEqual(start.AddMinutes(50), receiver.Requests[2].Single().RepeatDelivery!.Value.SlotUtc);
        Assert.AreEqual(1L, model.Items[0].ReminderRepeat!.AdditionalAlertsSent);
    });

    [TestMethod]
    public void ExistingWindowsToastRecoversAcceptanceAndCompletionRemovesTheSeries() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var first = Repeating(clock, 5, null);
        var store = new Store { Items = [first] };
        using var model = Model(store);
        var receiver = new Receiver();
        List<Guid> removed = [];
        var delivery = new ChecklistRepeatDelivery(first.ReminderRepeat!.Id, first.ReminderAtUtc!.Value);
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, receiver.Submit, clock,
            repeatHistory: [new(first.Id, null, delivery)], removeRepeat: removed.Add);
        Assert.HasCount(0, receiver.Requests);
        Assert.AreEqual(delivery.SlotUtc, store.Items.Single().ReminderRepeat!.LastSlotUtc);
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
        var task = model.Items.Single();
        task.IsChecked = true;
        scheduler.CheckNow();
        CollectionAssert.AreEqual(new[] { delivery.SeriesId }, removed);
        Assert.IsNull(store.Items.Single().ReminderRepeat);
        Assert.IsFalse(model.IsCurrentRepeatNotification(new(first.Id, null, delivery)));
    });

    [TestMethod]
    public void RestartWithoutAToastContinuesTheSeriesAndPreservesUnrelatedEdits() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var start = clock.GetUtcNow();
        var store = new Store { Items = [Repeating(clock, 10, null)] };
        var receiver = new Receiver();
        using (var model = Model(store))
        using (var scheduler = Scheduler(model, receiver.Submit, clock))
        {
            var item = model.Items.Single();
            item.Notes = "unrelated edit";
            Assert.IsTrue(model.TryFlushPendingItems(out _));
        }

        // indefinitely repeating after a dismissed or expired toast needs no Windows history entry.
        clock.Advance(TimeSpan.FromMinutes(10));
        using var reopened = Model(store);
        using var resumed = Scheduler(reopened, receiver.Submit, clock);
        var task = reopened.Items.Single();
        Assert.AreEqual(1L, task.ReminderRepeat!.AdditionalAlertsSent);
        Assert.AreEqual(start.AddMinutes(20), task.ReminderRepeat.NextSlotUtc);
        Assert.AreEqual("unrelated edit", task.Notes);
        Assert.HasCount(2, receiver.Requests);
    });

    [TestMethod]
    [DataRow("stop repeats", ChecklistNotificationStyle.Standard)]
    [DataRow("clear reminder", ChecklistNotificationStyle.Standard)]
    [DataRow("delete", ChecklistNotificationStyle.Standard)]
    [DataRow("reschedule", ChecklistNotificationStyle.Standard)]
    [DataRow("switch style", ChecklistNotificationStyle.Standard)]
    [DataRow("stop repeats", ChecklistNotificationStyle.Persistent)]
    [DataRow("clear reminder", ChecklistNotificationStyle.Persistent)]
    [DataRow("delete", ChecklistNotificationStyle.Persistent)]
    [DataRow("complete", ChecklistNotificationStyle.Persistent)]
    [DataRow("reschedule", ChecklistNotificationStyle.Persistent)]
    [DataRow("switch style", ChecklistNotificationStyle.Persistent)]
    public void EditingOrDeletingADeliveredSeriesRemovesItsToast(string change, ChecklistNotificationStyle style) => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var store = new Store { Items = [Repeating(clock, 5, null) with { NotificationStyle = style }] };
        using var model = Model(store);
        var receiver = new Receiver();
        List<Guid> removed = [];
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, receiver.Submit, clock, removeRepeat: removed.Add);
        var task = model.Items.Single();
        var old = task.ReminderRepeat!;
        var activation = new ChecklistReminderActivation(task.Id, null, receiver.Requests.Single().Single().RepeatDelivery);
        var future = clock.GetUtcNow().AddHours(1);
        switch (change)
        {
            case "stop repeats":
                Assert.IsTrue(model.TrySetSchedule(task, null, null, task.ReminderAtUtc, false, task.NotificationStyle, null, out _));
                Assert.IsNotNull(task.ReminderAtUtc);
                break;
            case "clear reminder":
                Assert.IsTrue(model.TrySetSchedule(task, null, null, null, false, task.NotificationStyle, null, out _));
                Assert.IsNull(task.ReminderAtUtc);
                break;
            case "delete":
                model.RemoveItem(task);
                break;
            case "complete":
                task.IsChecked = true;
                break;
            case "reschedule":
                Assert.IsTrue(model.TrySetSchedule(task, null, null, future, false, task.NotificationStyle, null, out _, old.Policy));
                Assert.AreNotEqual(old.Id, task.ReminderRepeat!.Id);
                Assert.AreEqual(0L, task.ReminderRepeat.AdditionalAlertsSent);
                Assert.AreEqual(future, task.ReminderRepeat.NextSlotUtc);
                break;
            case "switch style":
                var otherStyle = style == ChecklistNotificationStyle.Standard
                    ? ChecklistNotificationStyle.Persistent : ChecklistNotificationStyle.Standard;
                Assert.IsTrue(model.TrySetSchedule(task, null, null, future, false, otherStyle, null, out _),
                    $"Expected rescheduling to {otherStyle} to succeed.");
                Assert.IsNull(task.ReminderRepeat);
                break;
        }
        scheduler.CheckNow();
        CollectionAssert.AreEqual(new[] { old.Id }, removed);
        Assert.IsFalse(model.IsCurrentRepeatNotification(activation));
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        Assert.HasCount(1, receiver.Requests);
    });

    [TestMethod]
    [DataRow(3, 8, 9, 3)]
    [DataRow(11, 1, 8, 1)]
    public void RepeatUsesUtcCadenceAcrossDaylightSavingsAndClockRollback(int month, int day, int startHour, int expectedLocalHour) => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var start = new DateTimeOffset(2026, month, day, startHour, 55, 0, TimeSpan.Zero);
        clock.SetUtc(start);
        using var model = Model(new Store { Items = [Repeating(clock, 10, null)] });
        var receiver = new Receiver();
        using var scheduler = Scheduler(model, receiver.Submit, clock);
        clock.Advance(TimeSpan.FromMinutes(10));
        scheduler.CheckNow();
        var second = receiver.Requests.Last().Single().RepeatDelivery!.Value.SlotUtc;
        Assert.AreEqual(start.AddMinutes(10), second);
        var local = TimeZoneInfo.ConvertTime(second, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        Assert.AreEqual(expectedLocalHour, local.Hour);
        Assert.AreEqual(5, local.Minute);
        clock.SetUtc(start);
        scheduler.CheckNow();
        clock.Advance(TimeSpan.FromMinutes(10));
        scheduler.CheckNow();
        Assert.HasCount(2, receiver.Requests);
    });

    [TestMethod]
    public void RepeatAcceptanceSaveRetryDoesNotResubmitOrChangeLegacyBatchMembership() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var repeat = Repeating(clock, 5, null);
        var store = new Store { Items = [Due(clock), Due(clock), repeat] };
        using var model = Model(store);
        var requests = new List<ChecklistReminder[]>();
        using var scheduler = Scheduler(model, reminders =>
        {
            requests.Add(reminders.ToArray());
            if (reminders[0].RepeatDelivery.HasValue) store.FailSave = true;
            return true;
        }, clock);
        Assert.HasCount(2, requests);
        Assert.HasCount(2, model.Items.Where(item => item.ReminderHandledAtUtc == requests[0][0].BatchUtc).ToArray());
        Assert.IsNotNull(model.Items.Last().ReminderRepeat!.LastSlotUtc);
        Assert.IsNull(store.Items.Last().ReminderRepeat!.LastSlotUtc);
        store.FailSave = false;
        clock.Advance(TimeSpan.FromSeconds(31));
        scheduler.CheckNow();
        Assert.HasCount(2, requests);
        Assert.AreEqual(model.Items.Last().ReminderRepeat, store.Items.Last().ReminderRepeat);
    });

    [TestMethod]
    public void TurningRepeatsOffDuringFirstSubmissionDoesNotSendTheOriginalTwice() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var original = Repeating(clock, 5, null);
        var store = new Store { Items = [original] };
        using var model = Model(store);
        var item = model.Items.Single();
        var requests = new List<ChecklistReminder>();
        var removed = new List<Guid>();
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, reminders =>
        {
            var reminder = reminders.Single();
            requests.Add(reminder);
            if (reminder.RepeatDelivery is not null)
                Assert.IsTrue(model.TrySetSchedule(item, null, null, item.ReminderAtUtc, false, item.NotificationStyle, null, out _));
            return true;
        }, clock, removeRepeat: removed.Add);

        scheduler.CheckNow();
        Assert.HasCount(1, requests);
        Assert.IsNull(item.ReminderRepeat);
        Assert.IsNotNull(store.Items.Single().ReminderHandledAtUtc);
        CollectionAssert.AreEqual(new[] { original.ReminderRepeat!.Id }, removed);
    });

    [TestMethod]
    [DataRow(2)]
    [DataRow(null)]
    public void UrgentAcknowledgmentSkipsUnresolvedSlotsWithoutSpendingTheAllowance(int? limit) => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var start = clock.GetUtcNow();
        var options = new ChecklistNotificationOptions { Acknowledgment = ChecklistAcknowledgment.Code, UrgentSound = ChecklistUrgentSound.Chime };
        using var model = Model(new Store
        {
            Items = [Repeating(clock, 5, limit) with { NotificationStyle = ChecklistNotificationStyle.Urgent, NotificationOptions = options }]
        });
        using var alerts = new ChecklistUrgentRemindersViewModel(model, Dispatcher.CurrentDispatcher, clock);
        var plays = 0;
        using var audio = new ChecklistUrgentReminderAudio(alerts, Dispatcher.CurrentDispatcher, () => true, clock, testPlay: () => plays++);
        alerts.Changed += (_, _) => audio.Refresh();
        var requests = new Receiver();
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => false, clock,
            reminders => requests.Submit(reminders) && model.TryCaptureUrgentOptions(reminders, out _) && alerts.TryAccept(reminders));
        var task = model.Items.Single();
        var first = alerts.Entries.Single();
        Assert.AreEqual(1, plays);
        Assert.IsTrue(alerts.TrySilence(first), $"Expected to silence occurrence {first.OccurrenceId}.");
        clock.Advance(TimeSpan.FromMinutes(16));
        scheduler.CheckNow();
        audio.CheckNow();
        Assert.AreSame(first, alerts.Entries.Single());
        Assert.AreEqual(1, plays);
        Assert.HasCount(1, requests.Requests);
        Assert.IsNull(task.ReminderOccurrence!.AcknowledgedAtUtc);
        Assert.AreEqual(0L, task.ReminderRepeat!.AdditionalAlertsSent);
        Acknowledge(first);
        Assert.AreEqual(start.AddMinutes(20), task.ReminderRepeat.NextSlotUtc);
        scheduler.CheckNow();
        Assert.IsEmpty(alerts.Entries);
        Assert.IsFalse(task.IsChecked, $"Acknowledging occurrence {first.OccurrenceId} completed the task.");

        clock.Advance(TimeSpan.FromMinutes(4));
        scheduler.CheckNow();
        var second = alerts.Entries.Single();
        Assert.AreNotEqual(first.OccurrenceId, second.OccurrenceId);
        Assert.AreEqual(start.AddMinutes(20), second.ScheduledAtUtc);
        Assert.AreEqual(ChecklistAcknowledgment.Code, second.Options.Acknowledgment);
        Assert.IsNull(task.ReminderOccurrence.SilencedAtUtc);
        Assert.AreEqual(2, plays);
        Assert.AreEqual(1L, task.ReminderRepeat.AdditionalAlertsSent);
        Assert.IsFalse(model.TryAcknowledgeUrgentReminder(task.Id, first.OccurrenceId, clock.GetUtcNow(), out _),
            $"Stale occurrence {first.OccurrenceId} should be rejected while {second.OccurrenceId} is active.");
        Acknowledge(second);
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        Acknowledge(alerts.Entries.Single());
        Assert.AreEqual(2L, task.ReminderRepeat.AdditionalAlertsSent);
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        Assert.HasCount(limit.HasValue ? 0 : 1, alerts.Entries);
        Assert.AreEqual(limit.HasValue ? 2L : 3L, task.ReminderRepeat.AdditionalAlertsSent);

        void Acknowledge(ChecklistUrgentReminderEntry entry)
        {
            entry.ConfirmationInput = entry.Item.ReminderOccurrence!.ConfirmationCode;
            Assert.IsTrue(alerts.TryAcknowledge(entry), $"Acknowledgment failed for {entry.OccurrenceId}: {alerts.StatusMessage}");
        }
    });

    [TestMethod]
    [DataRow("complete")]
    [DataRow("delete")]
    [DataRow("disable reminder")]
    [DataRow("disable repeats")]
    public void UrgentCancellationStopsFutureAlerts(string change) => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        using var model = Model(new Store { Items = [Repeating(clock, 5, null) with { NotificationStyle = ChecklistNotificationStyle.Urgent }] });
        using var alerts = new ChecklistUrgentRemindersViewModel(model, Dispatcher.CurrentDispatcher, clock);
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => false, clock, alerts.TryAccept);
        var task = model.Items.Single();
        var original = alerts.Entries.Single();
        if (change == "complete") task.IsChecked = true;
        else if (change == "delete") model.RemoveItem(task);
        else
            Assert.IsTrue(model.TrySetSchedule(task, null, null, change == "disable repeats" ? task.ReminderAtUtc : null,
                false, task.NotificationStyle, null, out var error), error);
        alerts.Refresh();
        if (change == "disable repeats")
        {
            Assert.AreSame(original, alerts.Entries.Single());
            original.ConfirmationInput = original.Item.ReminderOccurrence!.ConfirmationCode;
            Assert.IsTrue(alerts.TryAcknowledge(original), $"Expected occurrence {original.OccurrenceId} to remain actionable.");
        }
        Assert.IsEmpty(alerts.Entries);
        clock.Advance(TimeSpan.FromMinutes(30));
        scheduler.CheckNow();
        Assert.IsEmpty(alerts.Entries);
        Assert.IsNull(task.ReminderRepeat);
    });

    [TestMethod]
    public void UrgentRestartGroupsRecoveredAlertsAndCountsOnlyNewlyPresentedSlots() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var start = clock.GetUtcNow();
        var store = new Store
        {
            Items = [Repeating(clock, 5, null) with { NotificationStyle = ChecklistNotificationStyle.Urgent },
                Repeating(clock, 5, 1) with { NotificationStyle = ChecklistNotificationStyle.Urgent }]
        };
        ChecklistReminderOccurrence original;
        using (var model = Model(store))
        {
            var requests = new Receiver();
            using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => false, clock, requests.Submit);
            Assert.HasCount(2, requests.Requests.Single());
            foreach (var task in model.Items)
                Assert.IsTrue(model.TryAcknowledgeUrgentReminder(task.Id, task.ReminderOccurrence!.Id, start, out var initialError), initialError);
            clock.Advance(TimeSpan.FromMinutes(5));
            scheduler.CheckNow();
            Assert.HasCount(2, requests.Requests.Last());
            var first = model.Items[0];
            Assert.IsTrue(model.TrySilenceUrgentReminder(first.Id, first.ReminderOccurrence!.Id, clock.GetUtcNow(), out var silenceError), silenceError);
            original = first.ReminderOccurrence;
            var second = model.Items[1];
            Assert.IsTrue(model.TryAcknowledgeUrgentReminder(second.Id, second.ReminderOccurrence!.Id, clock.GetUtcNow(), out var ackError), ackError);
        }

        clock.Advance(TimeSpan.FromMinutes(32));
        using var recovered = Model(store);
        var deliveries = new Receiver();
        using var restarted = new ChecklistReminderScheduler(recovered, Dispatcher.CurrentDispatcher, _ => false, clock, deliveries.Submit);
        var active = recovered.Items[0];
        Assert.AreEqual(original, deliveries.Requests.Single().Single().Occurrence);
        Assert.IsNull(deliveries.Requests[0][0].RepeatDelivery);
        Assert.AreEqual(1L, active.ReminderRepeat!.AdditionalAlertsSent);
        Assert.IsTrue(recovered.TryAcknowledgeUrgentReminder(active.Id, original.Id, clock.GetUtcNow(), out var error), error);
        restarted.CheckNow();
        Assert.HasCount(1, deliveries.Requests);
        Assert.AreEqual(start.AddMinutes(40), active.ReminderRepeat.NextSlotUtc);
        clock.Advance(TimeSpan.FromMinutes(3));
        restarted.CheckNow();
        Assert.HasCount(2, deliveries.Requests);
        Assert.AreEqual(2L, active.ReminderRepeat.AdditionalAlertsSent);
    });

    [TestMethod]
    public void UrgentActionOnARestoredWindowBeforeTheDeliveryRetryCountsThatOccurrenceOnce() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var start = clock.GetUtcNow();
        var store = new Store { Items = [Repeating(clock, 5, 1) with { NotificationStyle = ChecklistNotificationStyle.Urgent }] };
        using var model = Model(store);
        var requests = new Receiver();
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => false, clock, requests.Submit);
        var task = model.Items.Single();
        Assert.IsTrue(model.TryAcknowledgeUrgentReminder(task.Id, task.ReminderOccurrence!.Id, start, out var firstError), firstError);
        requests.Success = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        var pending = requests.Requests.Last().Single().Occurrence!;
        Assert.AreEqual(pending.Id, store.Items.Single().ReminderOccurrence?.Id);
        Assert.AreEqual(0L, task.ReminderRepeat!.AdditionalAlertsSent);
        Assert.IsTrue(model.TryAcknowledgeUrgentReminder(task.Id, pending.Id, clock.GetUtcNow(), out var secondError), secondError);
        Assert.AreEqual(1L, task.ReminderRepeat.AdditionalAlertsSent);
        Assert.IsNull(task.ReminderRepeat.NextSlotUtc);
        clock.Advance(TimeSpan.FromMinutes(10));
        scheduler.CheckNow();
        Assert.HasCount(2, requests.Requests);
    });

    [TestMethod]
    [DataRow(ChecklistWindowsNotificationSound.RepeatingAlarm)]
    [DataRow(ChecklistWindowsNotificationSound.Reminder)]
    public async Task PersistentRepeatsWaitForRemovalAndKeepTheirConfiguredSound(ChecklistWindowsNotificationSound repeatSound)
    {
        var seriesId = Guid.NewGuid();
        var start = new Clock().GetUtcNow();
        var first = new ChecklistReminder(Guid.NewGuid(), start, "task", Style: ChecklistNotificationStyle.Persistent,
            Options: new() { PersistentSound = ChecklistWindowsNotificationSound.RepeatingAlarm }, RepeatDelivery: new(seriesId, start));
        var removal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Guid> removed = [];
        List<ChecklistNotificationContent> submitted = [];
        using var notifications = new ChecklistNotifications(_ => { }, content => { submitted.Add(content); return true; },
            id => { removed.Add(id); return removal.Task; });
        Assert.IsTrue(await notifications.TrySubmitPersistentRepeatAsync([first], () => true), "The initial alarm should be submitted.");
        Assert.IsEmpty(removed);
        for (var index = 1; index <= 2; index++)
        {
            var repeat = first with { RepeatDelivery = new(seriesId, start.AddMinutes(index)), Options = new() { PersistentSound = repeatSound } };
            var delivery = notifications.TrySubmitPersistentRepeatAsync([repeat], () => true);
            Assert.HasCount(index, submitted, $"Repeat {index} was submitted before removal completed.");
            Assert.HasCount(index, removed);
            Assert.AreEqual(seriesId, removed[^1]);
            removal.SetResult();
            Assert.IsTrue(await delivery, $"Repeat {index} should be submitted after removal.");
            Assert.AreEqual(repeatSound, submitted[^1].Sound);
            Assert.AreEqual(repeat.RepeatDelivery, submitted[^1].RepeatDelivery);
            removal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    [TestMethod]
    public void PersistentRepeatCanceledDuringPreviousToastRemovalIsNotSubmitted() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var saved = Repeating(clock, 5, null) with { NotificationStyle = ChecklistNotificationStyle.Persistent };
        using var model = Model(new Store { Items = [saved] });
        var removal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ChecklistNotificationContent> submitted = [];
        var replacement = Task.FromResult(false);
        using var notifications = new ChecklistNotifications(_ => { }, content => { submitted.Add(content); return true; }, _ => removal.Task);
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => false, clock,
            submitPersistentRepeat: (reminders, canSubmit) => replacement = notifications.TrySubmitPersistentRepeatAsync(reminders, canSubmit));
        var task = model.Items.Single();
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        Assert.IsFalse(replacement.IsCompleted, "The repeat should wait for the previous notification to be removed.");
        Assert.IsTrue(model.TrySetSchedule(task, null, null, null, false, task.NotificationStyle, null, out var error), error);
        removal.SetResult();
        PumpUntil(() => replacement.IsCompleted);
        Assert.HasCount(1, submitted, "Only the original notification should be submitted.");
        Assert.IsNull(task.ReminderHandledAtUtc);
        Assert.IsNull(scheduler.LastError);
    });

    [TestMethod]
    public void SwitchingStylesRetainsProgressAndRevokesOldActions() => WpfTestHost.Invoke(() =>
    {
        var clock = new Clock();
        var start = clock.GetUtcNow();
        var store = new Store { Items = [Repeating(clock, 5, 3) with { NotificationStyle = ChecklistNotificationStyle.Persistent, Notes = "keep" }] };
        using var model = Model(store);
        var windows = new Receiver();
        var urgent = new Receiver();
        List<Guid> removed = [];
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, windows.Submit, clock, urgent.Submit,
            removeRepeat: removed.Add);
        var task = model.Items.Single();
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        var activation = new ChecklistReminderActivation(task.Id, null, windows.Requests.Last().Single().RepeatDelivery);
        var old = task.ReminderRepeat!;
        Switch(ChecklistNotificationStyle.Urgent);
        scheduler.CheckNow();
        CollectionAssert.AreEqual(new[] { old.Id }, removed);
        Assert.IsFalse(model.IsCurrentRepeatNotification(activation), $"Notification from series {old.Id} remained actionable.");
        Assert.AreNotEqual(old.Id, task.ReminderRepeat!.Id);
        Assert.AreEqual(old.NextSlotUtc, task.ReminderRepeat.NextSlotUtc);
        Assert.AreEqual(1L, task.ReminderRepeat.AdditionalAlertsSent);
        Assert.IsEmpty(urgent.Requests);
        using (var recovered = Model(store))
            Assert.IsNull(recovered.Items.Single().ReminderOccurrence);

        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        var occurrence = urgent.Requests.Single().Single().Occurrence!;
        Switch(ChecklistNotificationStyle.Standard);
        Assert.IsFalse(model.TrySilenceUrgentReminder(task.Id, occurrence.Id, clock.GetUtcNow(), out _),
            $"Old Urgent occurrence {occurrence.Id} remained actionable.");
        Assert.AreEqual(2L, task.ReminderRepeat!.AdditionalAlertsSent);
        Assert.AreEqual(start, task.ReminderAtUtc);
        Assert.AreEqual("keep", task.Notes);
        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.CheckNow();
        Assert.AreEqual(ChecklistNotificationStyle.Standard, windows.Requests.Last().Single().Style);
        Assert.IsNull(task.ReminderRepeat.NextSlotUtc);

        void Switch(ChecklistNotificationStyle style) =>
            Assert.IsTrue(model.TrySetSchedule(task, task.DueDate, task.DueTime, task.ReminderAtUtc, false, style,
                task.NotificationOptions, out var error, task.ReminderRepeat!.Policy), error);
    });

    private static ChecklistItemState Repeating(Clock clock, int minutes, int? additionalAlerts)
    {
        var first = clock.GetUtcNow();
        return Due(clock) with { ReminderAtUtc = first, ReminderRepeat = ChecklistReminderRepeat.Start(new(minutes, additionalAlerts), first) };
    }

    private static ChecklistItemState Due(Clock clock, string text = "task") =>
        new() { Id = Guid.NewGuid(), Text = text, ReminderAtUtc = clock.GetUtcNow().AddMinutes(-1) };

    private static ChecklistWindowViewModel Model(IChecklistContentService service) =>
        new(service, Dispatcher.CurrentDispatcher, action => action(), false);

    private static ChecklistReminderScheduler Scheduler(ChecklistWindowViewModel model,
        Func<IReadOnlyList<ChecklistReminder>, bool> submit, TimeProvider? clock = null) =>
        new(model, Dispatcher.CurrentDispatcher, submit, clock);

    private sealed class Receiver
    {
        internal List<ChecklistReminder[]> Requests { get; } = [];
        internal bool Success { get; set; } = true;
        internal bool Submit(IReadOnlyList<ChecklistReminder> reminders)
        {
            Requests.Add(reminders.ToArray());
            return Success;
        }
    }

    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; set; } = [];
        internal bool FailSave { get; set; }
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        public ChecklistContentLoadResult LoadItems() { Loads++; return new(Items, [], []); }
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            Saves++;
            if (FailSave) throw new IOException("save failed");
            Items = items.ToArray();
            return new(null);
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan elapsed) { _utc += elapsed; _timestamp += elapsed.Ticks; }
        internal void SetUtc(DateTimeOffset utc) => _utc = utc;
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => frame.Continue = !complete() && elapsed.Elapsed < TimeSpan.FromSeconds(3);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(complete(), "The reminder timer did not deliver within three seconds.");
    }
}
