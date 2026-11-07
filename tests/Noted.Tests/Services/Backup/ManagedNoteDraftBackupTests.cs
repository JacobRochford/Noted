using System.IO;
using System.Text.Json;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ManagedNoteDraftBackupTests
{
    [TestMethod]
    public void DraftBackingMetadataRecoveryAndSessionRoundTripAcrossAppDataRoots()
    {
        using var source = new BackupFixture();
        var export = source.Export();
        using var target = new BackupFixture(createDraft: false);
        var preview = target.Backup.GetBackupImportPreview(export);
        Assert.IsTrue(preview.IsValid, preview.Error);
        Assert.IsTrue(preview.Files.Any(file => file.LogicalPath == "app/Drafts/2026-11-05_10-20-30.txt"));
        Assert.IsTrue(preview.Files.Any(file => file.LogicalPath == "app/Drafts/2026-11-05_10-20-30.txt.json"));

        Assert.AreEqual(FullBackupStatus.RestoreScheduled, target.Backup.ScheduleBackupImportRestore(export,
            preview.BackupId, preview.VerificationToken!).Status);
        Assert.AreEqual(FullBackupStatus.Restored, target.Backup.ApplyPendingUserBackupRestore().Status);

        var session = new NoteEditorSessionService(target.AppData).Load().Session;
        Assert.AreEqual(target.DraftPath, session.ActiveFilePath);
        var recovery = new NoteRecoveryService(target.AppData);
        Assert.AreEqual("unsaved draft content", recovery.LoadDraft(target.DraftPath).Draft!.Content);
        var storage = new NoteDraftStorage(target.AppData);
        Assert.AreEqual("timestamp template", storage.Read(target.DraftPath)!.InitialContent);
        var loaded = new NoteEditorPersistence(new NoteContentService(target.AppData), recovery,
            new NoteEditorSessionService(target.AppData), draftStorage: storage).LoadWorkspace(true);
        Assert.AreEqual(target.DraftPath, loaded.Documents.Single().FilePath);
        Assert.AreEqual("unsaved draft content", loaded.Documents.Single().Content);
        Assert.IsEmpty(Directory.GetFiles(target.Notes));
    }

    [TestMethod]
    public void ImportRejectsExistingDraftFamilyWithoutChangingLocalData()
    {
        using var source = new BackupFixture();
        var export = source.Export();
        using var target = new BackupFixture();
        new NoteRecoveryService(target.AppData).SaveDraft(target.DraftPath, "local unfinished work");

        var preview = target.Backup.GetBackupImportPreview(export);

        Assert.IsFalse(preview.IsValid);
        Assert.AreEqual("local unfinished work", new NoteRecoveryService(target.AppData).LoadDraft(target.DraftPath).Draft!.Content);
        Assert.AreEqual("timestamp template", File.ReadAllText(target.DraftPath));
    }

    [TestMethod]
    public void InterruptedRestoreResumesMatchingDraftFilesWithoutCreatingAnotherDraft()
    {
        using var source = new BackupFixture();
        var export = source.Export();
        using var target = new BackupFixture(createDraft: false);
        var preview = target.Backup.GetBackupImportPreview(export);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, target.Backup.ScheduleBackupImportRestore(export,
            preview.BackupId, preview.VerificationToken!).Status);
        Directory.CreateDirectory(Path.GetDirectoryName(target.DraftPath)!);
        File.Copy(source.DraftPath, target.DraftPath);
        File.Copy(NoteDraftStorage.GetMetadataPath(source.DraftPath), NoteDraftStorage.GetMetadataPath(target.DraftPath));

        Assert.AreEqual(FullBackupStatus.Restored, target.Backup.ApplyPendingUserBackupRestore().Status);

        var loaded = new NoteEditorPersistence(new NoteContentService(target.AppData), new NoteRecoveryService(target.AppData),
            new NoteEditorSessionService(target.AppData), draftStorage: new NoteDraftStorage(target.AppData)).LoadWorkspace(true);
        Assert.AreEqual(target.DraftPath, loaded.Documents.Single().FilePath);
        Assert.AreEqual("unsaved draft content", loaded.Documents.Single().Content);
    }

    [TestMethod]
    public void DraftCreatedAfterImportSchedulingBlocksRestoreBeforeOtherFilesAreWritten()
    {
        using var source = new BackupFixture();
        var export = source.Export();
        using var target = new BackupFixture(createDraft: false);
        var preview = target.Backup.GetBackupImportPreview(export);
        Assert.IsTrue(preview.IsValid, preview.Error);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, target.Backup.ScheduleBackupImportRestore(export,
            preview.BackupId, preview.VerificationToken!).Status);
        new NoteDraftStorage(target.AppData).Create(target.DraftPath, "new local content");

        Assert.AreEqual(FullBackupStatus.Failed, target.Backup.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("new local content", File.ReadAllText(target.DraftPath));
        Assert.IsFalse(File.Exists(Path.Combine(target.AppData, "session", "editor-workspace.json")));
    }

    [TestMethod]
    public void PendingCleanupAndSessionProofRebaseWithoutRestoringADuplicate()
    {
        using var source = new BackupFixture();
        var destination = Path.Combine(source.Notes, "saved.md");
        File.WriteAllText(destination, "unsaved draft content");
        var storage = new NoteDraftStorage(source.AppData);
        var token = storage.PrepareCleanup(source.DraftPath, "unsaved draft content", destination: destination);
        new NoteEditorSessionService(source.AppData).Save(new NoteEditorSession
        {
            Tabs = [new() { FilePath = destination }], ActiveFilePath = destination,
            CompletedDraftTransitions = new Dictionary<string, Guid> { [source.DraftPath] = token }
        });
        var export = source.Export();
        using var target = new BackupFixture(createDraft: false);
        var preview = target.Backup.GetBackupImportPreview(export);
        Assert.IsTrue(preview.IsValid, preview.Error);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, target.Backup.ScheduleBackupImportRestore(export,
            preview.BackupId, preview.VerificationToken!).Status);
        Assert.AreEqual(FullBackupStatus.Restored, target.Backup.ApplyPendingUserBackupRestore().Status);
        var session = new NoteEditorSessionService(target.AppData);
        Assert.AreEqual(token, session.Load().Session.CompletedDraftTransitions[target.DraftPath]);
        Assert.AreEqual(Path.Combine(target.Notes, "saved.md"), new NoteDraftStorage(target.AppData).Read(target.DraftPath)!.SavedFilePath);

        var loaded = new NoteEditorPersistence(new NoteContentService(target.AppData), new NoteRecoveryService(target.AppData),
            session, draftStorage: new NoteDraftStorage(target.AppData)).LoadWorkspace(true);

        Assert.AreEqual(Path.Combine(target.Notes, "saved.md"), loaded.Documents.Single().FilePath);
        Assert.IsFalse(File.Exists(target.DraftPath));
        Assert.IsNull(new NoteRecoveryService(target.AppData).LoadDraft(target.DraftPath).Draft);
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly TemporaryTestDirectory _directory = new();
        internal string AppData => _directory.File("app");
        internal string Notes => _directory.File("notes");
        internal string DraftPath => Path.Combine(AppData, "Drafts", "2026-11-05_10-20-30.txt");
        internal FullBackupService Backup { get; }

        internal BackupFixture(bool createDraft = true)
        {
            Directory.CreateDirectory(Notes);
            if (createDraft)
            {
                new NoteDraftStorage(AppData).Create(DraftPath, "timestamp template");
                new NoteRecoveryService(AppData).SaveDraft(DraftPath, "unsaved draft content");
                new NoteEditorSessionService(AppData).Save(new NoteEditorSession
                {
                    Tabs = [new() { FilePath = DraftPath }], ActiveFilePath = DraftPath
                });
            }
            Backup = new FullBackupService(AppData, Notes);
        }

        internal string Export()
        {
            var result = Backup.CreateOrUpdateUserBackup();
            Assert.AreEqual(FullBackupStatus.Created, result.Status, result.Message);
            var export = _directory.File("drafts.notedbackup");
            var exported = Backup.ExportUserBackup(export);
            Assert.IsTrue(exported.Success, exported.Message);
            return export;
        }

        public void Dispose() => _directory.Dispose();
    }
}
