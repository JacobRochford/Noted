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
