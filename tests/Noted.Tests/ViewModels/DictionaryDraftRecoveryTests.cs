using System.IO;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class DictionaryDraftRecoveryTests
{
    [TestMethod]
    public void RestartRestoresBothKindsOfDraftWithoutPublishingThem() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = Seed(directory.Path);
        Guid newId;
        using (var model = Open(content, directory.Path))
        {
            EditExisting(model).DraftDefinition = "draft definition";
            var added = model.AddItem(beginEdit: true);
            added.DraftWord = "new draft";
            newId = added.Id;
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        }

        Assert.AreEqual("accepted", content.LoadItems().Items.Single().Description);
        using var restored = Open(content, directory.Path);
        Assert.IsTrue(restored.Items[0].IsEditing);
        Assert.AreEqual("draft definition", restored.Items[0].DraftDefinition);
        Assert.AreEqual("accepted", restored.Items[0].Definition);
        var recovered = restored.Items.Single(item => item.IsNew);
        Assert.AreEqual(newId, recovered.Id);
        Assert.AreEqual("new draft", recovered.DraftWord);
    });

    [TestMethod]
    public void InterruptedSaveUsesTheCommitReceiptAndDoesNotReopenTheDraft() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = Seed(directory.Path);
        var path = directory.File("dictionary-drafts.json");
        using (var model = Open(content, directory.Path))
        {
            var item = model.AddItem(beginEdit: true);
            item.DraftWord = "explicitly saved";
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
            var interruptedSnapshot = File.ReadAllBytes(path);
            Assert.IsTrue(model.TrySaveEntry(item, out error), error);
            File.WriteAllBytes(path, interruptedSnapshot);
        }

        using var restored = Open(content, directory.Path);
        Assert.HasCount(2, restored.Items);
        Assert.IsFalse(restored.Items.Any(item => item.IsEditing));
        Assert.AreEqual("explicitly saved", content.LoadItems().Items.Last().Word);
    });

    [TestMethod]
    public void FailedCancelKeepsDraftAndRetryDurablyDiscardsIt() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = Seed(directory.Path);
        using (var model = Open(content, directory.Path))
        {
            var item = model.AddItem(beginEdit: true);
            item.DraftWord = "keep until discard succeeds";
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
            using (File.Open(directory.File("dictionary-drafts.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(model.TryCancelEntry(item, out error));
                Assert.IsTrue(item.IsEditing && model.Items.Contains(item));
            }
            using (var recovered = Open(content, directory.Path))
                Assert.AreEqual(item.DraftWord, recovered.Items.Single(entry => entry.IsNew).DraftWord);
            Assert.IsTrue(model.TryCancelEntry(item, out error), error);
        }
        using var restored = Open(content, directory.Path);
        Assert.HasCount(1, restored.Items);
        Assert.IsFalse(restored.Items.Single().IsEditing);
    });

    [TestMethod]
    public void RepairedIdMustReachDiskBeforeTheDraftCanBeWritten() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("dictionary.json");
        File.WriteAllText(path, """[{"Word":"historical","Description":"accepted"}]""");
        var content = new DictionaryContentService(directory.Path);
        using var model = Open(content, directory.Path);
        var item = EditExisting(model);
        item.DraftDefinition = "unfinished";
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(model.TryFlushPendingItems(out _));
            Assert.IsFalse(File.Exists(directory.File("dictionary-drafts.json")));
            Assert.IsTrue(item.IsEditing);
        }
        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(item.Id, content.LoadItems().Items.Single().Id);
        using var restored = Open(content, directory.Path);
        Assert.AreEqual(item.Id, restored.Items.Single().Id);
        Assert.AreEqual("unfinished", restored.Items.Single().DraftDefinition);
    });

    [TestMethod]
    public void RestoringABackupKeepsDifferentDraftRevisionsAndDoesNotResurrectACancelledDraft() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        var content = Seed(app);
        var backups = new FullBackupService(app, notes);
        Guid cancelledId;
        using (var model = Open(content, app))
        {
            var existing = EditExisting(model);
            existing.DraftDefinition = "archived revision";
            var cancelled = model.AddItem(beginEdit: true);
            cancelledId = cancelled.Id;
            cancelled.DraftWord = "discard this";
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
            var created = backups.CreateOrUpdateUserBackup();
            Assert.AreEqual(FullBackupStatus.Created, created.Status, created.Message);
            existing.DraftDefinition = "local revision";
            Assert.IsTrue(model.TryCancelEntry(cancelled, out error), error);
        }

        var scheduled = backups.ScheduleUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, scheduled.Status, scheduled.Message);
        var restored = backups.ApplyPendingUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.Restored, restored.Status, restored.Message);
        using var reopened = Open(content, app);
        Assert.AreEqual("accepted", content.LoadItems().Items.Single().Description);
        Assert.AreEqual("local revision", reopened.Items.Single(item => !item.IsNew).DraftDefinition);
        Assert.AreEqual("archived revision", reopened.Items.Single(item => item.IsNew).DraftDefinition);
        Assert.IsFalse(reopened.Items.Any(item => item.Id == cancelledId));
    });

    [TestMethod]
    public void FullBackupExportAndImportKeepDraftsSeparateFromAcceptedEntries() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        var content = Seed(app);
        using (var model = Open(content, app))
        {
            EditExisting(model).DraftDefinition = "portable draft";
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        }
        var source = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, source.CreateOrUpdateUserBackup().Status);
        var archive = directory.File("portable.notedbackup");
        Assert.IsTrue(source.ExportUserBackup(archive).Success);

        var targetApp = directory.File("target-app");
        var targetNotes = directory.File("target-notes");
        Directory.CreateDirectory(targetApp);
        Directory.CreateDirectory(targetNotes);
        var target = new FullBackupService(targetApp, targetNotes);
        var preview = target.GetBackupImportPreview(archive);
        Assert.IsTrue(preview.IsValid, preview.Error);
        var scheduled = target.ScheduleBackupImportRestore(archive, preview.BackupId, preview.VerificationToken!);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, scheduled.Status, scheduled.Message);
        var restored = target.ApplyPendingUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.Restored, restored.Status, restored.Message);
        var targetContent = new DictionaryContentService(targetApp);
        Assert.AreEqual("accepted", targetContent.LoadItems().Items.Single().Description);
        using var recovered = Open(targetContent, targetApp);
        Assert.AreEqual("portable draft", recovered.Items.Single().DraftDefinition);
        Assert.IsTrue(recovered.Items.Single().IsEditing);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChangedOrDeletedBaselineLeavesTheDraftSeparate(bool deleted) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = Seed(directory.Path);
        using (var model = Open(content, directory.Path))
        {
            EditExisting(model).DraftDefinition = "keep this";
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        }
        var accepted = content.LoadItems().Items.Single();
        content.SaveItems(deleted ? [] : [accepted with { Description = "replacement" }]);
        using var restored = Open(content, directory.Path);
        Assert.AreEqual("keep this", restored.Items.Single(item => item.IsNew).DraftDefinition);
        Assert.AreEqual(deleted ? 0 : 1, content.LoadItems().Items.Count);
        if (!deleted)
            Assert.AreEqual("replacement", restored.Items.Single(item => !item.IsNew).Definition);
    });

    private static DictionaryItem EditExisting(DictionaryWindowViewModel model)
    {
        var item = model.Items.Single();
        item.BeginEdit();
        return item;
    }

    private static DictionaryContentService Seed(string path)
    {
        var content = new DictionaryContentService(path);
        content.SaveItems([new() { Id = Guid.NewGuid(), Word = "word", Description = "accepted" }]);
        return content;
    }

    private static DictionaryWindowViewModel Open(DictionaryContentService content, string directory) =>
        new(content, Dispatcher.CurrentDispatcher, action => action(), false, new DictionaryDraftStore(directory));
}
