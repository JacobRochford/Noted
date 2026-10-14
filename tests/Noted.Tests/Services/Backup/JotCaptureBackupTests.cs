using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.ViewModels;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class JotCaptureBackupTests
{
    [TestMethod]
    public void UserRecentAndBeforeRestoreBackupsIncludeDraftIdentityAndText()
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "unfinished\r\nthought" };
        var service = CreateService(directory, draft);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        AssertDraft(service.GetUserBackupPreview(), service, draft);

        var changed = new JotCaptureDraft { Text = "newer draft" };
        new JotCaptureDraftService(directory.File("app")).Save(changed);
        service = new FullBackupService(directory.File("app"), directory.File("notes"));
        var recent = service.CreateRecentBackup();
        Assert.AreEqual(FullBackupStatus.Created, recent.Status);
        Assert.AreEqual(changed, ReadDraft(Path.Combine(recent.BackupPath!, "files", "app", "jot-capture-draft.json")));
        var protection = service.CreateBeforeRestoreBackup();
        Assert.AreEqual(FullBackupStatus.Created, protection.Status);
        Assert.AreEqual(changed, ReadDraft(Path.Combine(protection.BackupPath!, "files", "app", "jot-capture-draft.json")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExportImportAndRestoreKeepDraftIdentityAndLocalUnsavedWork(bool hasLocalDraft)
    {
        using var source = new TemporaryTestDirectory();
        using var target = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "portable unfinished thought" };
        var service = CreateService(source, draft);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var archive = source.File("draft.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(archive).Success);
        var localDraft = new JotCaptureDraft { Text = hasLocalDraft ? "local unsaved work" : "" };
        var targetService = CreateService(target, localDraft);
        var preview = targetService.GetBackupImportPreview(archive);
        Assert.IsTrue(preview.IsValid, preview.Error);
        var file = preview.Files.Single(file => file.LogicalPath == "app/jot-capture-draft.json");
        Assert.AreEqual("Quick Jot", file.Category);
        var content = targetService.ReadBackupImportFile(archive, preview.BackupId, preview.VerificationToken!, file);
        Assert.IsTrue(content.Success, content.Error);
        Assert.AreEqual(draft, JotCaptureDraftService.ValidateBackupDraft(content.Data!));
        Assert.AreEqual(FullBackupStatus.RestoreScheduled,
            targetService.ScheduleBackupImportRestore(archive, preview.BackupId, preview.VerificationToken!).Status);
        var result = targetService.ApplyPendingUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.Restored, result.Status);
        Assert.AreEqual(hasLocalDraft ? localDraft : draft, ReadDraft(target.File("app", "jot-capture-draft.json")));
        if (hasLocalDraft)
        {
            Assert.IsNotNull(result.Warning);
            File.Delete(archive);
            var conflicts = Directory.GetFiles(Path.Combine(targetService.BackupDirectory,
                PersistencePaths.JotCaptureConflictsDirectory), "*.json");
            Assert.HasCount(1, conflicts);
            Assert.AreEqual(draft, ReadDraft(conflicts.Single()));
            Assert.HasCount(0, Directory.GetDirectories(targetService.BackupDirectory, "pending-import-*"));
            targetService = new FullBackupService(target.File("app"), target.File("notes"));
            Assert.AreEqual(draft, ReadDraft(conflicts.Single()));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RestoreKeepsDifferentUnfinishedLocalDraftAndReportsIt(bool incomingIsEmpty)
    {
        using var directory = new TemporaryTestDirectory();
        var incoming = new JotCaptureDraft { Text = incomingIsEmpty ? "" : "older draft" };
        var service = CreateService(directory, incoming);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var current = new JotCaptureDraft { Text = "new unsaved work" };
        new JotCaptureDraftService(directory.File("app")).Save(current);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);

        var result = service.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Restored, result.Status);
        StringAssert.Contains(result.Warning!, "unfinished local Quick Jot draft was kept");
        Assert.AreEqual(current, ReadDraft(directory.File("app", "jot-capture-draft.json")));
    }

    [TestMethod]
    public void ClearedDraftBackupDoesNotRestoreTheSubmittedRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateService(directory, new JotCaptureDraft { Text = "submitted" });
        var cleared = new JotCaptureDraft();
        new JotCaptureDraftService(directory.File("app")).Save(cleared);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(cleared, new JotCaptureDraftService(directory.File("app")).Load(out _));
        Assert.AreEqual("", ReadDraft(directory.File("app", "jot-capture-draft.json.bak"))!.Text);
    }

    [TestMethod]
    public void OlderBackupWithoutDraftLeavesLocalDraftUntouched()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("notes"));
        File.WriteAllText(directory.File("notes", "one.txt"), "note");
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var current = new JotCaptureDraft { Text = "new work" };
        new JotCaptureDraftService(directory.File("app")).Save(current);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(current, ReadDraft(directory.File("app", "jot-capture-draft.json")));
    }

    [TestMethod]
    public void BackupIncludesRecoverableDraftWhenPrimaryIsMissing()
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "recoverable text" };
        var service = CreateService(directory, draft);
        new JotCaptureDraftService(directory.File("app")).Save(new JotCaptureDraft { Text = "later text" });
        File.Delete(directory.File("app", "jot-capture-draft.json"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        AssertDraft(service.GetUserBackupPreview(), service, draft);
        Assert.IsFalse(File.Exists(directory.File("app", "jot-capture-draft.json")));
    }

    [TestMethod]
    public void PartialRestoreRetryKeepsDraftIdentityAndDoesNotDuplicateSubmission() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "captured before interruption" };
        var service = CreateService(directory, draft);
        new JotsContentService(directory.File("app")).SaveItems([new JotItemState { Id = draft.Id, Details = draft.Text }]);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        new JotCaptureDraftService(directory.File("app")).Save(new JotCaptureDraft());
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        using (var fileLock = new FileStream(directory.File("notes", "one.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(draft, ReadDraft(directory.File("app", "jot-capture-draft.json")));
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        using var workspace = new JotsWorkspace(new JotsContentService(directory.File("app")), Dispatcher.CurrentDispatcher);
        using var capture = new JotCaptureViewModel(workspace, new JotCaptureDraftService(directory.File("app")), Dispatcher.CurrentDispatcher);
        Assert.IsTrue(capture.TrySave());
        Assert.HasCount(1, workspace.Items);
        Assert.AreEqual("", capture.Text);
    });

    [TestMethod]
    public void RestoredJotWithConflictingIdentityCannotClearRetainedDraft() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "older captured text" };
        var service = CreateService(directory, draft);
        new JotsContentService(directory.File("app")).SaveItems([new JotItemState { Id = draft.Id, Details = draft.Text }]);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var current = draft with { Text = "different unsaved text" };
        new JotCaptureDraftService(directory.File("app")).Save(current);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        using var workspace = new JotsWorkspace(new JotsContentService(directory.File("app")), Dispatcher.CurrentDispatcher);
        using var capture = new JotCaptureViewModel(workspace, new JotCaptureDraftService(directory.File("app")), Dispatcher.CurrentDispatcher);
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual(current.Text, capture.Text);
        Assert.AreEqual(current, ReadDraft(directory.File("app", "jot-capture-draft.json")));
        capture.Text += " reviewed";
        Assert.IsTrue(capture.TrySave());
        Assert.HasCount(2, workspace.Items);
    });

    [TestMethod]
    public void UnreadableLocalDraftStopsRestoreBeforeOtherWritesAndCanRetry()
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "backup text" };
        var service = CreateService(directory, draft);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(directory.File("notes", "one.txt"), "new note text");
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        using (var fileLock = new FileStream(directory.File("app", "jot-capture-draft.json"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
            Assert.AreEqual("new note text", File.ReadAllText(directory.File("notes", "one.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
        }
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("note", File.ReadAllText(directory.File("notes", "one.txt")));
    }

    [TestMethod]
    public void RestoreKeepsRecoverableBackupWhenLocalPrimaryIsCorrupt()
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateService(directory, new JotCaptureDraft { Text = "older text" });
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var current = new JotCaptureDraft { Text = "recoverable newer text" };
        var store = new JotCaptureDraftService(directory.File("app"));
        store.Save(current);
        store.Save(new JotCaptureDraft { Text = "damaged later" });
        File.WriteAllText(directory.File("app", "jot-capture-draft.json"), "{broken");
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        var result = service.ApplyPendingUserBackupRestore();
        Assert.AreEqual(FullBackupStatus.Restored, result.Status);
        Assert.IsNotNull(result.Warning);
        Assert.AreEqual(current, ReadDraft(directory.File("app", "jot-capture-draft.json")));
        Assert.HasCount(1, Directory.GetFiles(directory.File("app"), "*.corrupt-*"));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{}]")]
    [DataRow("[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Text\":\"text\"}]")]
    [DataRow("[{\"Text\":null}]")]
    [DataRow("[{},{}]")]
    public void MalformedDraftStopsBackupCreation(string json)
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateService(directory, new JotCaptureDraft());
        File.WriteAllText(directory.File("app", "jot-capture-draft.json"), json);
        Assert.AreEqual(FullBackupStatus.Failed, service.CreateOrUpdateUserBackup().Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClearedDraftCannotReturnFromFallbackWhenPrimaryIsLost(bool corruptPrimary)
    {
        using var directory = new TemporaryTestDirectory();
        var store = new JotCaptureDraftService(directory.Path);
        store.Save(new JotCaptureDraft { Text = "intentionally discarded" });
        store.Save(new JotCaptureDraft());
        var primary = directory.File(PersistencePaths.JotCaptureDraftFile);
        if (corruptPrimary)
            File.WriteAllText(primary, "{broken");
        else
            File.Delete(primary);
        Assert.AreEqual("", new JotCaptureDraftService(directory.Path).Load(out _).Text);
    }

    [TestMethod]
    public void HistoricalEmptyPrimaryRepairsStaleFallbackWithoutChangingIdentity()
    {
        using var directory = new TemporaryTestDirectory();
        var primary = directory.File(PersistencePaths.JotCaptureDraftFile);
        const string empty = "[{\"Id\":\"e6e7f35d-645b-420d-b56d-7bdd257b06df\",\"Text\":\"\"}]";
        const string stale = "[{\"Id\":\"24058252-5ef4-4d2b-93eb-9c6f446b294e\",\"Text\":\"already discarded\"}]";
        File.WriteAllText(primary, empty);
        File.WriteAllText(primary + ".bak", stale);
        var store = new JotCaptureDraftService(directory.Path);
        var loaded = store.Load(out var warning);
        Assert.IsNull(warning);
        Assert.AreEqual(new Guid("e6e7f35d-645b-420d-b56d-7bdd257b06df"), loaded.Id);
        File.Delete(primary);
        Assert.AreEqual(loaded, new JotCaptureDraftService(directory.Path).Load(out _));
    }

    [TestMethod]
    [DataRow("[{}]")]
    [DataRow("[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Text\":\"bad\"}]")]
    [DataRow("[{\"Id\":\"e6e7f35d-645b-420d-b56d-7bdd257b06df\",\"Text\":null}]")]
    [DataRow("[{},{}]")]
    public void SemanticCorruptionUsesValidFallbackWithoutInventingIdentity(string json)
    {
        using var directory = new TemporaryTestDirectory();
        var saved = new JotCaptureDraft { Text = "recover me" };
        var store = new JotCaptureDraftService(directory.Path);
        store.Save(saved);
        store.Save(new JotCaptureDraft { Text = "later" });
        File.WriteAllText(directory.File(PersistencePaths.JotCaptureDraftFile), json);
        Assert.AreEqual(saved, new JotCaptureDraftService(directory.Path).Load(out var warning));
        Assert.IsNotNull(warning);
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    [TestMethod]
    public void InterruptedClearKeepsPrimaryAndRetryLeavesOnlyEmptyFallback()
    {
        using var directory = new TemporaryTestDirectory();
        var store = new JotCaptureDraftService(directory.Path);
        var draft = new JotCaptureDraft { Text = "unfinished" };
        store.Save(draft);
        var primary = directory.File(PersistencePaths.JotCaptureDraftFile);
        using (var fileLock = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => store.Save(new JotCaptureDraft()));
        Assert.AreEqual(draft, new JotCaptureDraftService(directory.Path).Load(out _));
        Assert.AreEqual("", ReadDraft(primary + ".bak")!.Text);
        new JotCaptureDraftService(directory.Path).Save(new JotCaptureDraft());
        File.Delete(primary);
        Assert.AreEqual("", new JotCaptureDraftService(directory.Path).Load(out _).Text);
    }

    [TestMethod]
    public void FailedConflictPreservationKeepsImportAndRequestForRetry()
    {
        using var source = new TemporaryTestDirectory();
        using var target = new TemporaryTestDirectory();
        var incoming = new JotCaptureDraft { Text = "incoming" };
        var sourceService = CreateService(source, incoming);
        Assert.AreEqual(FullBackupStatus.Created, sourceService.CreateOrUpdateUserBackup().Status);
        var archive = source.File("incoming.notedbackup");
        Assert.IsTrue(sourceService.ExportUserBackup(archive).Success);
        var local = new JotCaptureDraft { Text = "local" };
        var service = CreateService(target, local);
        var preview = service.GetBackupImportPreview(archive);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled,
            service.ScheduleBackupImportRestore(archive, preview.BackupId, preview.VerificationToken!).Status);
        var conflictDirectory = Path.Combine(service.BackupDirectory, PersistencePaths.JotCaptureConflictsDirectory);
        File.WriteAllText(conflictDirectory, "blocked directory");
        Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
        Assert.IsTrue(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
        Assert.AreEqual(local, ReadDraft(target.File("app", PersistencePaths.JotCaptureDraftFile)));
        File.Delete(archive);
        File.Delete(conflictDirectory);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(incoming, ReadDraft(Directory.GetFiles(conflictDirectory).Single()));
    }

    [TestMethod]
    [DataRow("[{}]")]
    [DataRow("[{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Text\":\"bad\"}]")]
    [DataRow("[{\"Id\":\"e6e7f35d-645b-420d-b56d-7bdd257b06df\",\"Text\":null}]")]
    [DataRow("[{},{}]")]
    public void MalformedHistoricalPayloadIsRejectedByImportAndLocalRestore(string json)
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateService(directory, new JotCaptureDraft { Text = "local" });
        var archive = directory.File("invalid.notedbackup");
        var id = WriteHistoricalArchive(archive, directory.File("app"), directory.File("notes"), json);
        Assert.IsFalse(service.GetBackupImportPreview(archive).IsValid);
        var snapshot = Path.Combine(service.BackupDirectory, "protected");
        ZipFile.ExtractToDirectory(archive, snapshot);
        JsonFileStore.Write(Path.Combine(service.BackupDirectory, "restore-request.json"),
            new FullBackupRestoreRequest(1, id, DateTime.UtcNow), options: BackupFormat.JsonOptions);
        Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("local", ReadDraft(directory.File("app", PersistencePaths.JotCaptureDraftFile))!.Text);
        Assert.IsTrue(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("[{\"Id\":\"e6e7f35d-645b-420d-b56d-7bdd257b06df\",\"Text\":\"historical text\"}]")]
    public void HistoricalDraftFormatStillImportsAndRestores(string json)
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateService(directory, new JotCaptureDraft());
        var archive = directory.File("historical.notedbackup");
        WriteHistoricalArchive(archive, directory.File("app"), directory.File("notes"), json);
        var preview = service.GetBackupImportPreview(archive);
        Assert.IsTrue(preview.IsValid, preview.Error);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled,
            service.ScheduleBackupImportRestore(archive, preview.BackupId, preview.VerificationToken!).Status);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(JotCaptureDraftService.ValidateBackupDraft(Encoding.UTF8.GetBytes(json)),
            ReadDraft(directory.File("app", PersistencePaths.JotCaptureDraftFile)));
    }

    private static Guid WriteHistoricalArchive(string path, string app, string notes, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var id = Guid.NewGuid();
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = archive.CreateEntry("files/app/jot-capture-draft.json").Open())
            stream.Write(bytes);
        var manifest = new
        {
            SchemaVersion = 1, SnapshotId = id, Role = "Protected", CreatedUtc = DateTime.UtcNow,
            AppDataRoot = app, NotesRoot = notes,
            Entries = new[] { new
            {
                LogicalPath = "app/jot-capture-draft.json",
                OriginalPath = Path.Combine(app, "jot-capture-draft.json"), Length = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ItemCount = (int?)null,
                ContentLength = (long?)null
            } }
        };
        using var manifestStream = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifestStream, manifest);
        return id;
    }

    [TestMethod]
    public void BackupPreparationFlushesPendingDraftAndBlocksFailedWrites() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateService(directory, new JotCaptureDraft());
        using var workspace = new JotsWorkspace(new JotsContentService(directory.File("app")), Dispatcher.CurrentDispatcher);
        using var capture = new JotCaptureViewModel(workspace,
            new JotCaptureDraftService(directory.File("app")), Dispatcher.CurrentDispatcher);
        var coordinator = new BackupCoordinator(service, new ShutdownFlushCoordinator(), () => directory.File("notes"));
        using var registration = coordinator.RegisterPreparationParticipant("Quick Jot", BackupPreparationPhase.BeforeSharedFlush,
            () => capture.TryFlushDraft(out var error) ? PersistenceSaveResult.Succeeded() : PersistenceSaveResult.Failed(error!),
            () => capture.Error);
        capture.Text = "pending text";
        using (var fileLock = new FileStream(directory.File("app", "jot-capture-draft.json"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.AreEqual(FullBackupStatus.Blocked, coordinator.CreateOrUpdateUserBackup().Status);
            Assert.AreEqual("pending text", capture.Text);
        }
        Assert.AreEqual(FullBackupStatus.Created, coordinator.CreateOrUpdateUserBackup().Status);
        var content = service.ReadUserBackupFile(service.GetUserBackupPreview().BackupId,
            service.GetUserBackupPreview().Files.Single(file => file.Category == "Quick Jot"));
        Assert.AreEqual("pending text", JotCaptureDraftService.ValidateBackupDraft(content.Data!)!.Text);
    });

    private static FullBackupService CreateService(TemporaryTestDirectory directory, JotCaptureDraft draft)
    {
        Directory.CreateDirectory(directory.File("notes"));
        File.WriteAllText(directory.File("notes", "one.txt"), "note");
        new JotCaptureDraftService(directory.File("app")).Save(draft);
        return new FullBackupService(directory.File("app"), directory.File("notes"));
    }

    private static JotCaptureDraft? ReadDraft(string path) =>
        JotCaptureDraftService.ValidateBackupDraft(File.ReadAllBytes(path));

    private static void AssertDraft(FullBackupPreview preview, FullBackupService service, JotCaptureDraft draft)
    {
        Assert.IsTrue(preview.IsValid, preview.Error);
        var file = preview.Files.Single(file => file.LogicalPath == "app/jot-capture-draft.json");
        Assert.AreEqual("Quick Jot", file.Category);
        var content = service.ReadUserBackupFile(preview.BackupId, file);
        Assert.IsTrue(content.Success, content.Error);
        Assert.AreEqual(draft, JotCaptureDraftService.ValidateBackupDraft(content.Data!));
    }
}
