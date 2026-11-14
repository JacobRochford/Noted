using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Reflection;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistReminderV1Tests
{
    [TestMethod]
    public void ReminderDefaultsUseFutureDueDateOrOneHourFromNow()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(new DateTime(2026, 6, 3, 9, 0, 0),
            ChecklistReminderTime.DefaultLocalTime(new DateTime(2026, 6, 3), now, TimeZoneInfo.Utc));
        Assert.AreEqual(new DateTime(2026, 6, 1, 13, 0, 0), ChecklistReminderTime.DefaultLocalTime(null, now, TimeZoneInfo.Utc));
        Assert.AreEqual(new DateTime(2026, 6, 1, 13, 0, 0),
            ChecklistReminderTime.DefaultLocalTime(new DateTime(2026, 5, 31), now, TimeZoneInfo.Utc));
    }

    [TestMethod]
    [DataRow(2026, 3, 8, "02:30", "does not exist")]
    [DataRow(2026, 11, 1, "01:30", "occurs twice")]
    public void DaylightSavingTimesRequireAnUnambiguousLocalTime(int year, int month, int day, string time, string message)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        Assert.IsFalse(ChecklistReminderTime.TryGetUtc(new DateTime(year, month, day), time,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), zone, out _, out var error));
        StringAssert.Contains(error!, message);
    }

    [TestMethod]
    public void LocalTimeConvertsToUtcAndPastOrMalformedTimesAreRejected()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        Assert.IsTrue(ChecklistReminderTime.TryGetUtc(new DateTime(2026, 6, 1), "09:30", now, zone, out var utc, out _));
        Assert.AreEqual(new DateTimeOffset(2026, 6, 1, 16, 30, 0, TimeSpan.Zero), utc);
        Assert.IsFalse(ChecklistReminderTime.TryGetUtc(new DateTime(2025, 1, 1), "09:30", now, zone, out _, out _));
        Assert.IsFalse(ChecklistReminderTime.TryGetUtc(new DateTime(2026, 6, 1), "24:30", now, zone, out _, out _));
        Assert.IsFalse(ChecklistReminderTime.TryGetUtc(null, "09:30", now, zone, out _, out _));
    }

    [TestMethod]
    public void CreationEditingRemovalAndSaveFailureUseTheExistingOwner() => WpfTestHost.Invoke(() =>
    {
        var store = new Store();
        using var model = Model(store);
        var item = model.AddItem()!;
        var reminder = DateTimeOffset.UtcNow.AddDays(1);
        Assert.IsTrue(model.TrySetReminder(item, reminder, out _));
        Assert.IsTrue(item.HasActiveReminder);
        Assert.AreEqual(reminder, store.Items.Single().ReminderAtUtc);
        item.ReminderHandledAtUtc = DateTimeOffset.UtcNow;
        Assert.IsFalse(item.HasActiveReminder);
        Assert.IsTrue(model.TrySetReminder(item, reminder.AddHours(1), out _));
        Assert.IsTrue(item.HasActiveReminder);
        Assert.IsNull(item.ReminderHandledAtUtc);
        Assert.IsFalse(model.TrySetReminder(item, DateTimeOffset.UtcNow.AddDays(-1), out _));
        Assert.AreEqual(reminder.AddHours(1), item.ReminderAtUtc);
        store.FailSave = true;
        Assert.IsFalse(model.TrySetReminder(item, reminder.AddHours(2), out _));
        Assert.IsTrue(model.HasPersistenceError);
        store.FailSave = false;
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.AreEqual(reminder.AddHours(2), store.Items.Single().ReminderAtUtc);
        Assert.IsTrue(model.TrySetReminder(item, null, out _));
        Assert.IsFalse(item.HasActiveReminder);
        Assert.IsNull(store.Items.Single().ReminderAtUtc);
        item.IsChecked = true;
        Assert.IsFalse(model.TrySetReminder(item, reminder, out _));
    });

    [TestMethod]
    public void IndividualAndLargeGroupedNotificationsKeepBoundedTaskSpecificArguments()
    {
        var batch = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var individual = ChecklistNotificationContent.Create([new(id, batch, "task", batch)]);
        Assert.AreEqual(id.ToString("D"), individual.Arguments["taskId"]);
        Assert.IsTrue(ChecklistReminderActivation.TryParse(individual.Arguments, out var request));
        Assert.AreEqual(id, request!.TaskId);
        var group = ChecklistNotificationContent.Create(Enumerable.Range(0, 1000)
            .Select(_ => new ChecklistReminder(Guid.NewGuid(), batch, "task", batch)).ToArray());
        Assert.IsFalse(group.Arguments.ContainsKey("taskId"));
        Assert.IsTrue(ChecklistReminderActivation.TryParse(group.Arguments, out var grouped));
        Assert.AreEqual(batch, grouped!.BatchUtc);
        Assert.IsTrue(grouped.Serialize().Length < 512);
        Assert.AreEqual(grouped, ChecklistReminderActivation.Deserialize(grouped.Serialize()));
    }

    [TestMethod]
    public void AdapterSubmissionFailureDoesNotBecomeSuccess()
    {
        var batch = DateTimeOffset.UtcNow;
        var reminder = new ChecklistReminder(Guid.NewGuid(), batch, "task", batch);
        using var rejected = new ChecklistNotifications(_ => { }, _ => false);
        Assert.IsFalse(rejected.TrySubmit([reminder]));
        using var throwing = new ChecklistNotifications(_ => { }, _ => throw new IOException("delivery failed"));
        Assert.IsFalse(throwing.TrySubmit([reminder]));
        ChecklistNotificationContent? submitted = null;
        using var accepted = new ChecklistNotifications(_ => { }, content => { submitted = content; return true; });
        Assert.IsTrue(accepted.TrySubmit([reminder]));
        Assert.IsNotNull(submitted);
    }

    [TestMethod]
    public void StartupMarkersCannotBecomeFileOpenRequestsAndInvalidActivationsAreRejected()
    {
        Assert.HasCount(0, ChecklistReminderActivation.FileArguments(["----AppNotificationActivated:", "-Embedding"]));
        CollectionAssert.AreEqual(new[] { @"C:\notes\one.txt", @"C:\notes\two.txt" },
            ChecklistReminderActivation.FileArguments([@"C:\notes\one.txt", @"C:\notes\two.txt"]).ToArray());
        Assert.IsFalse(ChecklistReminderActivation.TryParse(new Dictionary<string, string>
            { ["action"] = "checklistReminder", ["taskId"] = Guid.Empty.ToString() }, out _));
        Assert.IsNull(ChecklistReminderActivation.Deserialize("{broken"));
        Assert.IsNull(ChecklistReminderActivation.Deserialize("{\"TaskId\":null,\"BatchUtc\":null}"));
        var id = Guid.NewGuid();
        Assert.AreEqual(id, ChecklistReminderActivation.Deserialize(new ChecklistReminderActivation(id, null).Serialize())!.TaskId);
    }

    [TestMethod]
    public void NavigationRevealsMovedAndFilteredTasksAndIgnoresCompletedOrDeletedIds() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var model = Model(new ChecklistContentService(directory.Path));
        var item = model.AddItem()!;
        item.Text = "target";
        Assert.IsTrue(model.TryCreateTab("Work", out var tab));
        model.MoveItemToTab(item, tab!.Id);
        model.SelectTab(model.FindTab(ChecklistTab.AllId));
        model.SearchQuery = "does not match";
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), model);
        try
        {
            window.Show();
            model.SelectTab(model.FindTab(ChecklistTab.AllId));
            model.SearchQuery = "does not match";
            Assert.IsTrue(window.NavigateToReminder(item.Id));
            Assert.AreEqual(tab.Id, model.SelectedTab!.Id);
            Assert.AreEqual("", model.SearchQuery);
            Assert.AreSame(item, ((ListBox)window.FindName("ItemsList")).SelectedItem);
            model.MoveItemToTab(item, null);
            model.SelectTab(tab);
            Assert.IsTrue(model.SetDefaultTabVisibility(ChecklistTab.AllId, false));
            Assert.IsTrue(window.NavigateToReminder(item.Id));
            Assert.IsTrue(model.FindTab(ChecklistTab.AllId)!.IsVisible);
            item.IsChecked = true;
            Assert.IsFalse(model.RevealItem(item.Id, out _));
            Assert.IsFalse(model.RevealItem(Guid.NewGuid(), out _));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void SubmittedTextDoesNotClaimTheNotificationWasSeen()
    {
        var converter = new ChecklistReminderTextConverter();
        var text = (string)converter.Convert([DateTimeOffset.UtcNow, DateTimeOffset.UtcNow], typeof(string), null!, CultureInfo.InvariantCulture);
        StringAssert.Contains(text, "Submitted");
    }

    [TestMethod]
    public void GroupStampDoesNotIncludeAnEarlierDeliveryAtTheSameClockTime() => WpfTestHost.Invoke(() =>
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        using var model = Model(new Store());
        var batches = new List<ChecklistReminder[]>();
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher,
            request => { batches.Add(request.ToArray()); return true; }, new FixedClock(now));
        model.AddItem()!.ReminderAtUtc = now.AddMinutes(-1);
        scheduler.CheckNow();
        model.AddItem()!.ReminderAtUtc = now.AddMinutes(-1);
        model.AddItem()!.ReminderAtUtc = now.AddMinutes(-1);
        scheduler.CheckNow();
        Assert.HasCount(2, batches);
        Assert.AreNotEqual(batches[0][0].BatchUtc, batches[1][0].BatchUtc);
        var stamp = batches[1][0].BatchUtc;
        Assert.HasCount(2, model.Items.Where(item => item.ReminderHandledAtUtc == stamp).ToArray());
    });

    [TestMethod]
    public void ReminderDialogValidatesThenAppliesAndAllowsRemoval() => WpfTestHost.Invoke(() =>
    {
        Exception? failure = null;
        var dialog = new ChecklistReminderDialog(null, DateTime.Today.AddDays(2));
        dialog.Loaded += (_, _) =>
        {
            try
            {
                var date = (DatePicker)dialog.FindName("ReminderDate");
                var time = (TextBox)dialog.FindName("ReminderTime");
                var apply = (Button)dialog.FindName("ApplyButton");
                date.SelectedDate = DateTime.Today.AddDays(-1);
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(Visibility.Visible, ((TextBlock)dialog.FindName("ErrorText")).Visibility);
                date.SelectedDate = DateTime.Today.AddDays(2);
                time.Text = "14:30";
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception exception) { failure = exception; dialog.Close(); }
        };
        Assert.AreEqual(true, dialog.ShowDialog());
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.IsNotNull(dialog.ReminderAtUtc);
        var remove = new ChecklistReminderDialog(dialog.ReminderAtUtc, null);
        remove.Loaded += (_, _) => ((Button)remove.FindName("RemoveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(true, remove.ShowDialog());
        Assert.IsNull(remove.ReminderAtUtc);
    });

    [TestMethod]
    public void GroupMenuContainsOnlyTheAffectedTasksAndNavigatesById() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var model = Model(new ChecklistContentService(directory.Path));
        var batch = DateTimeOffset.UtcNow;
        var one = model.AddItem()!;
        one.Text = "first";
        one.ReminderAtUtc = batch.AddMinutes(-1);
        one.ReminderHandledAtUtc = batch;
        var two = model.AddItem()!;
        two.Text = "second";
        two.ReminderAtUtc = batch.AddMinutes(-1);
        two.ReminderHandledAtUtc = batch;
        model.AddItem()!.Text = "unrelated";
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), model);
        try
        {
            window.Show();
            window.OpenReminder(new(null, batch));
            var menu = (ContextMenu)typeof(ChecklistWindow).GetField("_reminderTaskMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.HasCount(2, menu.Items);
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Assert.AreSame(two, ((ListBox)window.FindName("ItemsList")).SelectedItem);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ActivationWaitsForAnInvalidDueDateToBeCorrected() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var model = Model(new ChecklistContentService(directory.Path));
        var editing = model.AddItem()!;
        editing.Text = "editing";
        var target = model.AddItem()!;
        target.Text = "target";
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), model);
        try
        {
            window.Show();
            editing.IsExpanded = true;
            window.UpdateLayout();
            var list = (ListBox)window.FindName("ItemsList");
            var row = (System.Windows.DependencyObject)list.ItemContainerGenerator.ContainerFromItem(editing);
            var picker = VisualTreeHelpers.FindDescendant<DatePicker>(row, "ItemDueDatePicker")!;
            var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker)!;
            Assert.IsTrue(editor.Focus());
            editor.Text = "not a date";
            window.OpenReminder(new(target.Id, null));
            Assert.AreNotSame(target, list.SelectedItem);
            editor.Text = "";
            model.SortMode = ChecklistSortMode.Priority;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreSame(target, list.SelectedItem);
        }
        finally { window.Close(); }
    });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [TestMethod]
    public void LargeGroupUpdatesTheListOnceAndPersistsEveryHandledTask() => WpfTestHost.Invoke(() =>
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        using var model = Model(new Store());
        for (var index = 0; index < 100; index++) model.AddItem()!.ReminderAtUtc = now.AddMinutes(-1);
        var refreshes = 0;
        model.ViewRefreshed += (_, _) => refreshes++;
        using var scheduler = new ChecklistReminderScheduler(model, Dispatcher.CurrentDispatcher, _ => true, new FixedClock(now));
        Assert.IsTrue(model.Items.All(item => item.ReminderHandledAtUtc is not null));
        Assert.AreEqual(1, refreshes);
    });

    private static ChecklistWindowViewModel Model(IChecklistContentService store) =>
        new(store, Dispatcher.CurrentDispatcher, action => action(), false);

    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; private set; } = [];
        internal bool FailSave { get; set; }
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            if (FailSave) throw new IOException("save failed");
            Items = items.ToArray();
            return new(null);
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
