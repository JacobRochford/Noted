using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistUrgentReminderStateTests
{
    private static readonly DateTimeOffset s_now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void HistoricalDefaultsAndUrgentStateRoundTrip() => WpfTestHost.Invoke(() =>
    {
        var historical = JsonSerializer.Deserialize<ChecklistItemState>("{\"Text\":\"old task\",\"ReminderAtUtc\":\"2030-01-01T11:00:00Z\"}")!;
        using var old = Model(new Store { Items = [historical] });
        Assert.AreEqual(ChecklistNotificationStyle.Standard, old.Items.Single().NotificationStyle);
        Assert.IsNull(old.Items.Single().ReminderOccurrence);
        var store = new Store();
        using (var model = Model(store))
        {
            var item = Urgent();
            model.Items.Add(item);
            Assert.IsTrue(model.TrySilenceUrgentReminder(item.Id, item.ReminderOccurrence!.Id, s_now, out var error), error);
            Assert.IsTrue(model.TryAcknowledgeUrgentReminder(item.Id, item.ReminderOccurrence.Id, s_now, out error), error);
            Assert.IsFalse(item.IsChecked);
        }
        var json = JsonSerializer.Serialize(store.Items);
        using var loaded = Model(new Store { Items = JsonSerializer.Deserialize<ChecklistItemState[]>(json)! });
        var occurrence = loaded.Items.Single().ReminderOccurrence!;
        Assert.AreEqual(s_now, occurrence.AcknowledgedAtUtc);
        Assert.AreEqual(s_now, occurrence.SilencedAtUtc);
        Assert.AreEqual(store.Items.Single().ReminderOccurrence, occurrence);
    });

    [TestMethod]
    public void ReschedulingReplacesCodeAndRejectsStaleActions() => WpfTestHost.Invoke(() =>
    {
        using var model = Model(new Store());
        var item = Urgent(); model.Items.Add(item);
        var first = item.ReminderOccurrence!;
        Assert.IsTrue(model.TrySilenceUrgentReminder(item.Id, first.Id, s_now, out _));
        item.ReminderAtUtc = s_now.AddHours(1);
        var next = item.ReminderOccurrence!;
        Assert.AreNotEqual(first.Id, next.Id);
        Assert.AreNotEqual(first.ConfirmationCode, next.ConfirmationCode);
        Assert.IsNull(next.AcknowledgedAtUtc); Assert.IsNull(next.SilencedAtUtc);
        Assert.IsFalse(model.TryAcknowledgeUrgentReminder(item.Id, first.Id, s_now.AddHours(2), out _));
        item.Text = "edited"; item.Notes = "details";
        Assert.AreEqual(next, item.ReminderOccurrence);
        Assert.IsTrue(model.TrySetReminder(item, item.ReminderAtUtc, out _));
        Assert.AreEqual(next.Id, item.ReminderOccurrence!.Id);
    });

    [TestMethod]
    public void FailedAcknowledgmentAndSilenceRemainUnresolved() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var model = Model(store);
        var item = Urgent(); model.Items.Add(item);
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        var occurrence = item.ReminderOccurrence!;
        store.Fail = true;
        Assert.IsFalse(model.TryAcknowledgeUrgentReminder(item.Id, occurrence.Id, s_now, out _));
        Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc);
        Assert.IsTrue(item.HasActiveReminder);
        Assert.IsFalse(model.TrySilenceUrgentReminder(item.Id, occurrence.Id, s_now, out _));
        Assert.IsNull(item.ReminderOccurrence.SilencedAtUtc);
        store.Fail = false;
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.IsNull(store.Items.Single().ReminderOccurrence!.AcknowledgedAtUtc);
        Assert.IsTrue(model.TryAcknowledgeUrgentReminder(item.Id, occurrence.Id, s_now, out _));
        Assert.IsFalse(item.HasActiveReminder);
    });

    [TestMethod]
    public void RecoveryGroupsDeliveredUrgentButDoesNotRepeatStandardOrAcknowledged() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); Guid firstId; string code;
        using (var model = Model(store))
        {
            var first = Urgent(); var second = Urgent();
            model.Items.Add(first); model.Items.Add(second);
            model.Items.Add(new ChecklistItem { Text = "standard", ReminderAtUtc = s_now.AddMinutes(-1) });
            firstId = first.Id; code = first.ReminderOccurrence!.ConfirmationCode;
            int standard = 0, urgent = 0;
            using var scheduler = Scheduler(model, _ => { standard++; return true; }, request =>
            { urgent++; Assert.AreEqual(2, request.Count); return true; });
            Assert.AreEqual(1, standard); Assert.AreEqual(1, urgent);
            scheduler.CheckNow(); Assert.AreEqual(1, urgent);
            Assert.IsTrue(model.TryAcknowledgeUrgentReminder(second.Id, second.ReminderOccurrence!.Id, s_now, out _));
            Assert.IsTrue(model.TrySilenceUrgentReminder(first.Id, first.ReminderOccurrence.Id, s_now, out _));
        }
        using var recovered = Model(store);
        var requests = new List<ChecklistReminder[]>();
        using var restarted = Scheduler(recovered, _ => { Assert.Fail("standard must not repeat"); return false; }, request =>
        { requests.Add(request.ToArray()); return true; });
        Assert.AreEqual(1, requests.Count); Assert.AreEqual(1, requests[0].Length);
        var reminder = requests[0][0]; Assert.AreEqual(firstId, reminder.TaskId);
        Assert.AreEqual(code, reminder.Occurrence!.ConfirmationCode);
        Assert.AreEqual(s_now, reminder.Occurrence.SilencedAtUtc);
        Assert.AreEqual(s_now.AddMinutes(-1), reminder.ReminderAtUtc);
    });

    [TestMethod]
    public void SubmissionAndHandledSaveFailuresRecoverWithoutLosingOrBursting() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var model = Model(store);
        model.Items.Add(Urgent()); var clock = new Clock(); int calls = 0;
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => true, clock,
            _ => { calls++; store.Fail = true; return true; });
        Assert.AreEqual(1, calls); Assert.IsNotNull(scheduler.LastError);
        scheduler.CheckNow(); Assert.AreEqual(1, calls);
        store.Fail = false; clock.Advance(); scheduler.CheckNow();
        Assert.AreEqual(1, calls); Assert.IsNull(scheduler.LastError);
        using var recovered = Model(store); int retries = 0; var retryClock = new Clock();
        using var restart = new ChecklistReminderScheduler(recovered, Dispatcher.CurrentDispatcher, _ => true, retryClock,
            _ => { retries++; return false; });
        Assert.AreEqual(1, retries); restart.CheckNow(); Assert.AreEqual(1, retries);
        retryClock.Advance(); restart.CheckNow(); Assert.AreEqual(2, retries);
        Assert.IsNull(recovered.Items.Single().ReminderOccurrence!.AcknowledgedAtUtc);
    });

    [TestMethod]
    public void CompletionDeletionDuplicationAndInvalidStoredOccurrenceAreSafe() => WpfTestHost.Invoke(() =>
    {
        using var model = Model(new Store()); var item = Urgent(); model.Items.Add(item);
        var occurrence = item.ReminderOccurrence!;
        item.IsChecked = true; item.IsChecked = false;
        Assert.IsNull(item.ReminderOccurrence); Assert.IsNull(item.ReminderAtUtc);
        Assert.IsFalse(model.TrySilenceUrgentReminder(item.Id, occurrence.Id, s_now, out _));
        item.ReminderAtUtc = s_now; model.Items.Remove(item);
        Assert.IsNull(item.ReminderOccurrence);
        var stored = new ChecklistItemState { Id = Guid.NewGuid(), NotificationStyle = ChecklistNotificationStyle.Urgent,
            ReminderAtUtc = s_now, ReminderHandledAtUtc = s_now,
            ReminderOccurrence = occurrence with { ScheduledAtUtc = s_now.AddDays(-1), AcknowledgedAtUtc = s_now } };
        using var restored = Model(new Store { Items = [stored, stored] });
        var repaired = restored.Items[0];
        Assert.IsNull(repaired.ReminderOccurrence!.AcknowledgedAtUtc);
        Assert.AreEqual(s_now, repaired.ReminderOccurrence.ScheduledAtUtc);
        Assert.IsTrue(repaired.HasActiveReminder);
        Assert.IsNull(restored.Items[1].ReminderOccurrence);
        Assert.IsNull(restored.Items[1].ReminderAtUtc);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UrgentStateSurvivesBackupRestoreAndRollingRecovery(bool repeating) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app"); var notes = directory.File("notes");
        Directory.CreateDirectory(app); Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(app, "settings.json"), "{\"SchemaVersion\":1}");
        ChecklistReminderOccurrence original;
        ChecklistReminderRepeat? progress = null;
        using (var model = Model(new ChecklistContentService(app)))
        {
            var item = Urgent(); model.Items.Add(item);
            if (repeating)
            {
                var first = s_now.AddMinutes(-6);
                item.ReminderAtUtc = first;
                item.ReminderHandledAtUtc = first;
                progress = ChecklistReminderRepeat.Start(new(5, 2), first).Accept(first, first, first)
                    .Accept(first, s_now.AddMinutes(-1), s_now.AddMinutes(-1));
                item.ReminderRepeat = progress;
                item.ReminderOccurrence = ChecklistReminderOccurrence.Create(progress.LastSlotUtc!.Value);
            }
            Assert.IsTrue(model.TrySilenceUrgentReminder(item.Id, item.ReminderOccurrence!.Id, s_now, out var error), error);
            original = item.ReminderOccurrence!;
        }
        var backup = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, backup.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(Path.Combine(app, "checklist.json"), "[]");
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, backup.ScheduleUserBackupRestore().Status);
        Assert.AreEqual(FullBackupStatus.Restored, backup.ApplyPendingUserBackupRestore().Status);
        var restoredJson = File.ReadAllText(Path.Combine(app, "checklist.json"));
        File.WriteAllText(Path.Combine(app, "checklist.json.bak"), restoredJson);
        File.WriteAllText(Path.Combine(app, "checklist.json"), "{broken");
        using var recovered = Model(new ChecklistContentService(app));
        Assert.IsTrue(recovered.RecoveryIssuesFoundThisRun);
        Assert.AreEqual(original, recovered.Items.Single().ReminderOccurrence);
        Assert.AreEqual(progress, recovered.Items.Single().ReminderRepeat);
        int received = 0;
        using var scheduler = Scheduler(recovered, _ => false, _ => { received++; return true; });
        Assert.AreEqual(1, received);
        Assert.AreEqual(progress, recovered.Items.Single().ReminderRepeat);
    });

    [TestMethod]
    public void UrgentWithoutReceiverStaysPendingAndStandardStillDelivers() => WpfTestHost.Invoke(() =>
    {
        using var model = Model(new Store()); model.Items.Add(Urgent());
        model.Items.Add(new ChecklistItem { ReminderAtUtc = s_now }); int standard = 0;
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher,
            request => { standard++; Assert.IsTrue(request.All(r => r.Style == ChecklistNotificationStyle.Standard)); return true; }, new Clock());
        Assert.AreEqual(1, standard); Assert.IsNotNull(scheduler.LastError);
        Assert.IsNull(model.Items[0].ReminderHandledAtUtc);
    });

    [TestMethod]
    public void FailedPreDeliverySaveAndThrownPresentationLeaveDurableOccurrencePending() => WpfTestHost.Invoke(() =>
    {
        var store = new Store { Fail = true }; using var model = Model(store);
        var item = Urgent(); model.Items.Add(item); var clock = new Clock(); int calls = 0;
        using (var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => true, clock,
            _ => { calls++; throw new IOException("presentation interrupted"); }))
        {
            Assert.AreEqual(0, calls);
            store.Fail = false; clock.Advance(); scheduler.CheckNow();
            Assert.AreEqual(1, calls);
            Assert.IsNull(store.Items.Single().ReminderHandledAtUtc);
            Assert.AreEqual(item.ReminderOccurrence, store.Items.Single().ReminderOccurrence);
        }
        using var recovered = Model(store);
        using var restarted = Scheduler(recovered, _ => false, _ => { calls++; return true; });
        Assert.AreEqual(2, calls);
    });

    [TestMethod]
    public void CancellationDuringAnotherStylesDeliveryDoesNotPresentTheCancelledTask() => WpfTestHost.Invoke(() =>
    {
        using var model = Model(new Store());
        model.Items.Add(new ChecklistItem { ReminderAtUtc = s_now.AddMinutes(-2) });
        var urgent = Urgent(); model.Items.Add(urgent); int received = 0;
        using var scheduler = Scheduler(model, _ => { urgent.IsChecked = true; return true; },
            _ => { received++; return true; });
        Assert.AreEqual(0, received); Assert.IsNull(urgent.ReminderOccurrence);
    });

    [TestMethod]
    public void DuplicationAndCompletionCancelRemindersButStyleChangesPreserveAcknowledgment() => WpfTestHost.Invoke(() =>
    {
        using var model = Model(new Store()); var item = Urgent(); model.Items.Add(item);
        Assert.IsTrue(model.TryAcknowledgeUrgentReminder(item.Id, item.ReminderOccurrence!.Id, s_now, out _));
        model.DuplicateItem(item);
        Assert.IsNull(model.Items[1].ReminderOccurrence); Assert.IsNull(model.Items[1].ReminderAtUtc);
        var occurrence = item.ReminderOccurrence;
        item.NotificationStyle = ChecklistNotificationStyle.Standard;
        Assert.AreEqual(occurrence, item.ReminderOccurrence);
        item.NotificationStyle = ChecklistNotificationStyle.Urgent;
        Assert.AreEqual(occurrence, item.ReminderOccurrence);
        Assert.IsNotNull(item.ReminderOccurrence!.AcknowledgedAtUtc);
        model.CheckAll(); model.UncheckAll();
        Assert.IsTrue(model.Items.All(task => task.ReminderOccurrence is null && task.ReminderAtUtc is null));
    });

    [TestMethod]
    public void WindowsAdapterDoesNotDowngradeUrgentOrUnknownStyles()
    {
        int submissions = 0;
        using var adapter = new ChecklistNotifications(_ => { }, _ => { submissions++; return true; });
        foreach (var style in new[] { ChecklistNotificationStyle.Urgent, (ChecklistNotificationStyle)99 })
            Assert.IsFalse(adapter.TrySubmit([new ChecklistReminder(Guid.NewGuid(), s_now, "task", Style: style)]));
        Assert.AreEqual(0, submissions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SuspensionDuringSuccessfulDeliveryStillRecordsAcceptance(bool urgent) => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var model = Model(store); var clock = new Clock();
        var item = new ChecklistItem { Text = "task",
            NotificationStyle = urgent ? ChecklistNotificationStyle.Urgent : ChecklistNotificationStyle.Standard,
            ReminderAtUtc = s_now.AddSeconds(5) };
        model.Items.Add(item); int calls = 0; ChecklistReminderScheduler? active = null;
        bool Accept(IReadOnlyList<ChecklistReminder> reminders)
        {
            calls++; active!.SetSuspended(true); return true;
        }
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, Accept, clock, Accept);
        active = scheduler; clock.Advance(); scheduler.CheckNow();
        Assert.IsNotNull(item.ReminderHandledAtUtc, "Successful acceptance must survive a shutdown cancellation or resume.");
        Assert.IsNotNull(store.Items.Single().ReminderHandledAtUtc);
        scheduler.SetSuspended(false); scheduler.CheckNow(); Assert.AreEqual(1, calls);
    });

    [TestMethod]
    public void LaterStyleReceivesSilenceAppliedDuringEarlierDelivery() => WpfTestHost.Invoke(() =>
    {
        using var model = Model(new Store());
        model.Items.Add(new ChecklistItem { ReminderAtUtc = s_now.AddMinutes(-2) });
        var urgent = Urgent(); model.Items.Add(urgent); int calls = 0;
        using var scheduler = Scheduler(model, _ =>
        {
            Assert.IsTrue(model.TrySilenceUrgentReminder(urgent.Id, urgent.ReminderOccurrence!.Id, s_now, out var error), error);
            return true;
        }, request =>
        {
            calls++; Assert.AreEqual(s_now, request.Single().Occurrence!.SilencedAtUtc); return true;
        });
        Assert.AreEqual(1, calls);
        Assert.IsNotNull(urgent.ReminderHandledAtUtc);
    });

    private static ChecklistItem Urgent() => new() { Text = "urgent", Notes = "details",
        NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = s_now.AddMinutes(-1) };
    private static ChecklistWindowViewModel Model(IChecklistContentService store) =>
        new(store, Dispatcher.CurrentDispatcher, action => action(), false);
    private static ChecklistReminderScheduler Scheduler(ChecklistWindowViewModel model,
        Func<IReadOnlyList<ChecklistReminder>, bool> standard, Func<IReadOnlyList<ChecklistReminder>, bool> urgent) =>
        new(model, Dispatcher.CurrentDispatcher, standard, new Clock(), urgent);
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => s_now.AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance() => _ticks += TimeSpan.FromSeconds(31).Ticks;
    }
    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; set; } = [];
        internal bool Fail { get; set; }
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            if (Fail) throw new IOException("test disk failure");
            Items = items.ToArray(); return new(null);
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
