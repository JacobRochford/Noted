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
