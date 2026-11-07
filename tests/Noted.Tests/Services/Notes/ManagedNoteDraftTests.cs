using System.IO;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ManagedNoteDraftTests
{
    [TestMethod]
    public void QuickNotesUseUniqueManagedFilesAndKeepTheirActualTimestampTemplate()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        settings.SaveTimestampPlacement(NoteTimestampPlacement.Top);
        using var files = new NoteFileService(settings);
        var storage = new NoteDraftStorage(directory.Path);

        var first = files.CreateNote().Note!;
        var second = files.CreateNote().Note!;

        Assert.AreNotEqual(first.FilePath, second.FilePath);
        Assert.IsTrue(storage.IsDraftPath(first.FilePath!));
        Assert.AreEqual(first.InitialContent, File.ReadAllText(first.FilePath!));
        Assert.IsTrue(first.InitialContent.Length > 0);
        Assert.AreEqual(first.InitialContent, storage.Read(first.FilePath!)!.InitialContent);
        Assert.IsEmpty(files.GetNotes());
        Assert.IsEmpty(Directory.GetFiles(files.NotesDirectory));
    }

    [TestMethod]
    public void ManagedStorageRejectsAJunctionAndDoesNotTreatSiblingPrefixesAsDrafts()
    {
        using var directory = new TemporaryTestDirectory();
        var storage = new NoteDraftStorage(directory.File("app"));
        Assert.IsFalse(storage.IsDraftPath(null));
        Assert.IsFalse(storage.IsDraftPath(string.Empty));
        var target = directory.File("unrelated");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "unrelated content");
        Assert.IsFalse(storage.ContainsPath(directory.File("app", "Drafts-other", "note.txt")));
        using var link = new TestFileSystemLink(storage.DirectoryPath, target);

        Assert.ThrowsExactly<IOException>(() => storage.Create(Path.Combine(storage.DirectoryPath, "new.txt"), "template"));

        Assert.AreEqual("unrelated content", File.ReadAllText(sentinel));
        Assert.IsFalse(File.Exists(Path.Combine(target, "new.txt")));
    }

    [TestMethod]
    public void BlockedDraftDirectoryDoesNotPreventBrowsingOrCreatingNamedNotes()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.File("app"));
        settings.SaveNotesDirectory(directory.File("notes"));
        var draftDirectory = Path.Combine(settings.AppDataDirectory, PersistencePaths.NoteDraftsDirectory);
        File.WriteAllText(draftDirectory, "blocks Quick Note storage");
        using var files = new NoteFileService(settings);

        var named = files.CreateNote("named note").Note!;

        Assert.AreEqual(named.FileName, files.GetNotes().Single().FileName);
        Assert.AreEqual(named.FilePath, Path.Combine(files.CurrentDirectory, named.FileName));
        Assert.ThrowsExactly<IOException>(() => files.CreateNote());
        Assert.AreEqual("blocks Quick Note storage", File.ReadAllText(draftDirectory));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ManagedFilesStayOutOfBrowserListingsWhenNotesFoldersOverlap(bool exactDraftFolder)
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var storage = new NoteDraftStorage(directory.Path);
        settings.SaveNotesDirectory(exactDraftFolder ? storage.DirectoryPath : directory.Path);
        using var files = new NoteFileService(settings);
        var draft = files.CreateNote().Note!;

        Assert.IsFalse(files.GetNotes().Any(note => note.FileName == draft.FileName));
        Assert.IsFalse(files.GetAllNoteKeys().Any(key => key.Contains("/Drafts/", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(files.TryGetNoteKey(draft.FilePath!, out _));
        Assert.IsFalse(files.GetFolders().Any(folder => folder.FileName == PersistencePaths.NoteDraftsDirectory));
        Assert.IsEmpty(files.GetNotesInSubfolder(PersistencePaths.NoteDraftsDirectory));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RecoveryRestoresManagedDraftsWithoutSessionMetadata(bool removeBacking)
    {
        using var fixture = new DraftFixture();
        fixture.Recovery.SaveDraft(fixture.Source, "recoverable edits");
        File.Delete(fixture.SessionPath);
        if (removeBacking)
            File.Delete(fixture.Source);

        var loaded = fixture.Restart(loadSession: false);

        var document = loaded.Documents.Single();
        Assert.AreEqual(fixture.Source, document.FilePath);
        Assert.AreEqual("recoverable edits", document.Content);
        Assert.AreEqual(fixture.InitialContent, document.InitialFileContent);
        Assert.IsTrue(document.IsDirty);
        Assert.IsTrue(document.UsesGeneratedName);
        Assert.IsFalse(document.IsMissing);
    }

    [TestMethod]
    public void UntouchedTimestampDraftRestoresWithoutBeingTreatedAsEdited()
    {
        using var fixture = new DraftFixture();
        var document = fixture.Restart(loadSession: false).Documents.Single();
        Assert.AreEqual(fixture.InitialContent, document.Content);
        Assert.IsFalse(document.IsDirty);
        Assert.IsTrue(document.UsesGeneratedName);
        fixture.Workspace.UpdateContent(fixture.Document, fixture.InitialContent + "user text", 0);
        Assert.IsTrue(fixture.Document.IsDirty);
    }

    [TestMethod]
    public void BackingFileWithoutMetadataIsKeptAsUnsavedUntilAnExplicitSave()
    {
        using var fixture = new DraftFixture();
        File.Delete(NoteDraftStorage.GetMetadataPath(fixture.Source));
        var restored = fixture.Restart(loadSession: false).Documents.Single();
        Assert.IsTrue(restored.IsDirty);
        Assert.IsNull(restored.InitialFileContent);
        var workspace = new NoteEditorWorkspace();
        workspace.Add(restored);
        workspace.Activate(restored);

        Assert.IsNull(fixture.Persistence().SaveManagedDraft(workspace, restored,
            fixture.Destination, new(fixture.Destination, null)));

        Assert.AreEqual(fixture.InitialContent, File.ReadAllText(fixture.Destination));
        Assert.IsFalse(File.Exists(fixture.Source));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SessionFailureBeforeOrAfterPersistenceKeepsSourceUntilRestart(bool failAfterWrite)
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        var failing = new FailingSession(fixture.Session, failAfterWrite);
        var persistence = fixture.Persistence(failing);

        Assert.ThrowsExactly<IOException>(() => persistence.SaveManagedDraft(fixture.Workspace,
            fixture.Document, fixture.Destination, new(fixture.Destination, null)));

        Assert.AreEqual("user edits", File.ReadAllText(fixture.Destination));
        Assert.AreEqual(fixture.InitialContent, File.ReadAllText(fixture.Source));
        Assert.AreEqual("user edits", fixture.Recovery.LoadDraft(fixture.Source).Draft!.Content);
        Assert.AreEqual(fixture.Source, fixture.Document.FilePath);
        Assert.IsTrue(fixture.Document.IsDirty);

        var restored = fixture.Restart();
        Assert.AreEqual(failAfterWrite ? fixture.Destination : fixture.Source, restored.Documents.Single().FilePath);
        Assert.AreEqual("user edits", restored.Documents.Single().Content);
        Assert.AreEqual(!failAfterWrite, File.Exists(fixture.Source));
    }

    [TestMethod]
    public void EarlierSessionProofCannotAuthorizeALaterFailedSave()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        var persistence = fixture.Persistence(new FailingSession(fixture.Session, true));
        Assert.ThrowsExactly<IOException>(() => persistence.SaveManagedDraft(fixture.Workspace,
            fixture.Document, fixture.Destination, new(fixture.Destination, null)));
        var firstToken = fixture.Storage.Read(fixture.Source)!.CleanupToken;
        fixture.Workspace.UpdateContent(fixture.Document, "new edits after the failed transition", 0);
        var blocked = fixture.Directory.File("blocked.md");
        Directory.CreateDirectory(blocked);

        Assert.Throws<IOException>(() => persistence.SaveManagedDraft(fixture.Workspace,
            fixture.Document, blocked, new(blocked, null)));
        Assert.AreNotEqual(firstToken, fixture.Storage.Read(fixture.Source)!.CleanupToken);

        var restored = fixture.Restart();
        Assert.AreEqual("new edits after the failed transition", restored.Documents.Single(document => document.FilePath == fixture.Source).Content);
        Assert.IsTrue(File.Exists(fixture.Source));
        Assert.AreEqual("new edits after the failed transition", fixture.Recovery.LoadDraft(fixture.Source).Draft!.Content);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CleanupRestoresTheDraftIfItsSavedDestinationIsMissingOrChanged(bool changed)
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        using (new FileStream(fixture.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
            fixture.Persistence().SaveManagedDraft(fixture.Workspace, fixture.Document, fixture.Destination, new(fixture.Destination, null));
        if (changed)
            File.WriteAllText(fixture.Destination, "other destination content");
        else
            File.Delete(fixture.Destination);

        var restored = fixture.Restart();

        var recovered = restored.Documents.Single(document => document.FilePath == fixture.Source);
        Assert.AreEqual("user edits", recovered.Content);
        Assert.IsTrue(recovered.IsDirty);
        Assert.IsTrue(restored.RecoveryIssues.Count > 0);
        Assert.IsTrue(File.Exists(fixture.Source));
        Assert.AreEqual("user edits", fixture.Recovery.LoadDraft(fixture.Source).Draft!.Content);
        Assert.IsNull(fixture.Storage.Read(fixture.Source)!.CleanupContentHash);
    }

    [TestMethod]
    public void ChangedRecoveryIsRestoredInsteadOfBeingHiddenByACompletedCleanup()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        using (new FileStream(fixture.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
            fixture.Persistence().SaveManagedDraft(fixture.Workspace, fixture.Document, fixture.Destination, new(fixture.Destination, null));
        fixture.Recovery.SaveDraft(fixture.Source, "newer recovery content");

        var restored = fixture.Restart();

        Assert.AreEqual("newer recovery content", restored.Documents.Single(document => document.FilePath == fixture.Source).Content);
        Assert.IsTrue(File.Exists(fixture.Source));
        Assert.AreEqual("newer recovery content", fixture.Recovery.LoadDraft(fixture.Source).Draft!.Content);
    }

    [TestMethod]
    public void UnreadableMetadataDoesNotHideValidRecoveryContent()
    {
        using var fixture = new DraftFixture();
        fixture.Recovery.SaveDraft(fixture.Source, "recoverable content");
        File.WriteAllText(NoteDraftStorage.GetMetadataPath(fixture.Source), "broken metadata");

        var restored = fixture.Restart();

        Assert.AreEqual("recoverable content", restored.Documents.Single().Content);
        Assert.IsTrue(restored.Documents.Single().IsDirty);
        Assert.IsNull(restored.Documents.Single().InitialFileContent);
        Assert.IsTrue(restored.RecoveryIssues.Count > 0);
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public void CleanupKeepsCorruptRecoveryArtifactsAndRequiresReview()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        using (new FileStream(fixture.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
            fixture.Persistence().SaveManagedDraft(fixture.Workspace, fixture.Document, fixture.Destination, new(fixture.Destination, null));
        var recoveryPath = fixture.Recovery.GetDraftFilePath(fixture.Source);
        const string partialRecovery = "{\"Content\":\"new partial recovery";
        File.WriteAllText(recoveryPath, partialRecovery);
        File.Delete(recoveryPath + ".bak");

        var restored = fixture.Restart();

        var recovered = restored.Documents.Single(document => document.FilePath == fixture.Source);
        Assert.IsTrue(recovered.IsDirty);
        Assert.IsNull(recovered.InitialFileContent);
        Assert.IsTrue(File.Exists(fixture.Source));
        var preserved = Directory.GetFiles(Path.GetDirectoryName(recoveryPath)!, Path.GetFileName(recoveryPath) + ".corrupt-*").Single();
        Assert.AreEqual(partialRecovery, File.ReadAllText(preserved));
    }

    [TestMethod]
    public void LogicallyInvalidMetadataFallsBackToItsValidBackup()
    {
        using var fixture = new DraftFixture();
        fixture.Storage.PrepareCleanup(fixture.Source, fixture.InitialContent);
        File.WriteAllText(NoteDraftStorage.GetMetadataPath(fixture.Source), "{\"InitialContent\":null}");

        Assert.AreEqual(fixture.InitialContent, fixture.Storage.Read(fixture.Source)!.InitialContent);
        Assert.IsTrue(File.Exists(fixture.Source));
    }

    [TestMethod]
    public void FailedDestinationWritePreservesOriginalAndRecovery()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        Directory.CreateDirectory(fixture.Destination);

        Assert.Throws<IOException>(() => fixture.Persistence().SaveManagedDraft(fixture.Workspace,
            fixture.Document, fixture.Destination, new(fixture.Destination, null)));

        Assert.AreEqual(fixture.Source, fixture.Document.FilePath);
        Assert.AreEqual(fixture.InitialContent, File.ReadAllText(fixture.Source));
        Assert.AreEqual("user edits", fixture.Restart().Documents.Single().Content);
    }

    [TestMethod]
    public void FailedBackingCleanupDoesNotReopenADuplicateAndRetriesAfterUnlocking()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        using (new FileStream(fixture.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var warning = fixture.Persistence().SaveManagedDraft(fixture.Workspace, fixture.Document,
                fixture.Destination, new(fixture.Destination, null));
            Assert.IsNotNull(warning);
            Assert.IsTrue(fixture.Storage.Read(fixture.Source)!.CleanupCommitted);
            Assert.IsTrue(File.Exists(fixture.Source));
            var restored = fixture.Restart();
            Assert.AreEqual(fixture.Destination, restored.Documents.Single().FilePath);
            Assert.IsTrue(restored.RecoveryIssues.Count > 0);
        }

        Assert.AreEqual(fixture.Destination, fixture.Restart().Documents.Single().FilePath);
        Assert.IsFalse(File.Exists(fixture.Source));
        Assert.IsNull(fixture.Storage.Read(fixture.Source));
        Assert.IsNull(fixture.Recovery.LoadDraft(fixture.Source).Draft);
    }

    [TestMethod]
    public void InterruptionBetweenBackingAndRecoveryCleanupIsReconciledWithoutSession()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        var recovery = new FailingRecovery(fixture.Recovery);
        var persistence = fixture.Persistence(recovery: recovery);
        Assert.IsNotNull(persistence.SaveManagedDraft(fixture.Workspace, fixture.Document,
            fixture.Destination, new(fixture.Destination, null)));
        Assert.IsFalse(File.Exists(fixture.Source));
        Assert.IsNotNull(fixture.Recovery.LoadDraft(fixture.Source).Draft);
        File.Delete(fixture.SessionPath);
        File.Delete(fixture.SessionPath + ".bak");

        Assert.IsEmpty(fixture.Restart(loadSession: false).Documents);
        Assert.IsNull(fixture.Recovery.LoadDraft(fixture.Source).Draft);
        Assert.IsNull(fixture.Storage.Read(fixture.Source));
        Assert.AreEqual("user edits", File.ReadAllText(fixture.Destination));
    }

    [TestMethod]
    public void SessionProofPreventsDuplicatesIfCommittedMetadataFallsBackToAnOlderVersion()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();
        using (new FileStream(fixture.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
            fixture.Persistence().SaveManagedDraft(fixture.Workspace, fixture.Document, fixture.Destination, new(fixture.Destination, null));
        File.WriteAllText(NoteDraftStorage.GetMetadataPath(fixture.Source), "broken");

        var restored = fixture.Restart();

        Assert.AreEqual(fixture.Destination, restored.Documents.Single().FilePath);
        Assert.IsFalse(File.Exists(fixture.Source));
        Assert.IsNull(fixture.Recovery.LoadDraft(fixture.Source).Draft);
    }

    [TestMethod]
    public void DiscardSessionFailureKeepsMeaningfulDraftAndSuccessfulDiscardRemovesIt()
    {
        using var fixture = new DraftFixture();
        fixture.Edit();

        Assert.ThrowsExactly<IOException>(() => fixture.Persistence(new FailingSession(fixture.Session, false))
            .DiscardManagedDraft(fixture.Workspace, fixture.Document));
        Assert.AreEqual("user edits", fixture.Restart().Documents.Single().Content);
        Assert.IsTrue(File.Exists(fixture.Source));

        Assert.IsNull(fixture.Persistence().DiscardManagedDraft(fixture.Workspace, fixture.Document));
        Assert.IsEmpty(fixture.Restart().Documents);
        Assert.IsFalse(File.Exists(fixture.Source));
        Assert.IsNull(fixture.Recovery.LoadDraft(fixture.Source).Draft);
    }

    internal sealed class DraftFixture : IDisposable
    {
        internal TemporaryTestDirectory Directory { get; } = new();
        internal string InitialContent => "2026-11-05 10:20:30\r\n\r\n\r\n";
        internal string Source { get; }
        internal string Destination => Directory.File("notes", "saved.md");
        internal string SessionPath => Directory.File("session", "editor-workspace.json");
        internal NoteDraftStorage Storage { get; }
        internal NoteRecoveryService Recovery { get; }
        internal NoteEditorSessionService Session { get; }
        internal NoteContentService Content { get; }
        internal NoteEditorWorkspace Workspace { get; } = new();
        internal OpenNoteDocument Document => Workspace.Documents.Single();

        internal DraftFixture()
        {
            Storage = new NoteDraftStorage(Directory.Path);
            Source = Path.Combine(Storage.DirectoryPath, "2026-11-05_10-20-30.txt");
            Storage.Create(Source, InitialContent);
            Recovery = new NoteRecoveryService(Directory.Path);
            Session = new NoteEditorSessionService(Directory.Path);
            Content = new NoteContentService(Directory.Path);
            Workspace.Add(new OpenNoteDocument(Source, InitialContent, InitialContent, false,
                usesGeneratedName: true, initialFileContent: InitialContent));
            Workspace.Activate(Document);
            Session.Save(NoteEditorPersistence.CreateSession(Workspace));
        }

        internal void Edit() => Workspace.UpdateContent(Document, "user edits", 4);
        internal NoteEditorPersistence Persistence(INoteEditorSessionService? session = null, INoteRecoveryService? recovery = null) =>
            new(Content, recovery ?? Recovery, session ?? Session, draftStorage: Storage);
        internal NoteEditorWorkspaceLoadResult Restart(bool loadSession = true) => Persistence().LoadWorkspace(loadSession);
        public void Dispose() => Directory.Dispose();
    }

    private sealed class FailingSession(INoteEditorSessionService inner, bool afterWrite) : INoteEditorSessionService
    {
        public NoteEditorSessionLoadResult Load() => inner.Load();
        public string? Save(NoteEditorSession session)
        {
            if (afterWrite) inner.Save(session);
            throw new IOException("Simulated interruption at session persistence.");
        }
    }

    private sealed class FailingRecovery(INoteRecoveryService inner) : INoteRecoveryService
    {
        public NoteRecoveryDraftLoadResult LoadDraft(string path) => inner.LoadDraft(path);
        public NoteRecoveryLoadResult LoadDrafts() => inner.LoadDrafts();
        public string? SaveDraft(string path, string content) => inner.SaveDraft(path, content);
        public void DeleteDraft(string path) => throw new IOException("Simulated interruption after backing cleanup.");
    }
}
