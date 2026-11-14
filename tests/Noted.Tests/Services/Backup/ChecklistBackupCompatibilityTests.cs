using System.IO;
using System.Windows.Threading;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistBackupCompatibilityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BackupRestoreRetainsHistoricalAndReminderRecords(bool hasReminder) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(notes);
        File.WriteAllText(directory.File("app", "settings.json"), "{\"SchemaVersion\":1,\"Revision\":7}");
        // literal persisted fields exercise the format independently of current model defaults.
        var content = hasReminder
            ? """
              [{"Id":"7832106e-e340-4b11-a8fa-f61d26605ce8","Text":"task","IsChecked":false,"Priority":3,"DueDate":"2026-12-02T00:00:00","ReminderAtUtc":"2026-12-01T17:00:00+00:00","ReminderHandledAtUtc":"2026-12-01T17:01:00+00:00","Notes":"details","CreatedAt":"2026-09-01T10:00:00"}]
              """
            : """
              [{"Text":"task","IsChecked":false,"Priority":3,"DueDate":"2026-12-02T00:00:00","Notes":"details","CreatedAt":"2026-09-01T10:00:00"}]
              """;
        File.WriteAllText(directory.File("app", "checklist.json"), content);
        var backups = new FullBackupService(app, notes);
        var created = backups.CreateOrUpdateUserBackup();
        Assert.AreEqual(FullBackupStatus.Created, created.Status, created.Message);
        File.WriteAllText(directory.File("app", "checklist.json"), "[]");
        var scheduled = backups.ScheduleUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, scheduled.Status, scheduled.Message);
        var restored = backups.ApplyPendingUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.Restored, restored.Status, restored.Message);
        Assert.AreEqual(content, File.ReadAllText(directory.File("app", "checklist.json")));

        using var model = new ChecklistWindowViewModel(new ChecklistContentService(app), Dispatcher.CurrentDispatcher, action => action(), false);
        var item = model.Items.Single();
        Assert.AreNotEqual(Guid.Empty, item.Id);
        Assert.AreEqual("task", item.Text);
        Assert.AreEqual("details", item.Notes);
        Assert.AreEqual(new DateTime(2026, 12, 2), item.DueDate);
        if (hasReminder)
        {
            Assert.AreEqual(Guid.Parse("7832106e-e340-4b11-a8fa-f61d26605ce8"), item.Id);
            Assert.AreEqual(new DateTimeOffset(2026, 12, 1, 17, 0, 0, TimeSpan.Zero), item.ReminderAtUtc);
            Assert.AreEqual(new DateTimeOffset(2026, 12, 1, 17, 1, 0, TimeSpan.Zero), item.ReminderHandledAtUtc);
        }
        else
        {
            Assert.IsNull(item.ReminderAtUtc);
            Assert.IsNull(item.ReminderHandledAtUtc);
        }
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
    });

    [TestMethod]
    public void RollingBackupRecoveryKeepsSavedIdentityAndReminder() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        const string recovered = """
            [{"Id":"7832106e-e340-4b11-a8fa-f61d26605ce8","Text":"recovered","ReminderAtUtc":"2026-12-01T17:00:00Z"}]
            """;
        File.WriteAllText(directory.File("checklist.json.bak"), recovered);
        File.WriteAllText(directory.File("checklist.json"), "{broken");
        using var model = new ChecklistWindowViewModel(new ChecklistContentService(directory.Path), Dispatcher.CurrentDispatcher, action => action(), false);
        Assert.IsTrue(model.RecoveryIssuesFoundThisRun);
        Assert.IsTrue(model.HasPersistenceError);
        var item = model.Items.Single();
        Assert.AreEqual(Guid.Parse("7832106e-e340-4b11-a8fa-f61d26605ce8"), item.Id);
        Assert.IsNotNull(item.ReminderAtUtc);
        item.Notes = "save after recovery";
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        var saved = new ChecklistContentService(directory.Path).LoadItems().Items.Single();
        Assert.AreEqual(item.Id, saved.Id);
        Assert.AreEqual(item.ReminderAtUtc, saved.ReminderAtUtc);
        Assert.IsTrue(model.RecoveryIssuesFoundThisRun);
    });
}
