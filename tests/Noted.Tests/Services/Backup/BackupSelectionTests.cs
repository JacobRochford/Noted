using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupSelectionTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void SaveConflictUsesExistingHistoryAndDocumentBackupSelection(bool openCopy, bool entireFolder)
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var appData = directory.File("app");
        var path = directory.File("notes", "note.md");
        File.WriteAllText(path, "original");
        var service = new NoteContentService(appData,
            new CallbackTimeProvider(() => File.WriteAllText(path, "external")), 20);
        var conflict = Assert.ThrowsExactly<NoteSaveConflictException>(() => service.Save(path, "editor edits"));
        var preserved = conflict.PreservedFilePath!;
        WriteSession(directory, openCopy ? [path, preserved] : [path]);

        var entries = catalog.Collect(out _, out _, includeEntireNotesFolder: entireFolder);

        Assert.AreEqual(openCopy || entireFolder, entries.Any(entry => entry.SourcePath == preserved));
        Assert.IsTrue(entries.Where(entry => entry.LogicalPath.StartsWith("app/note-history/", StringComparison.Ordinal)).Any(entry =>
        {
            using var history = JsonDocument.Parse(File.ReadAllText(entry.SourcePath));
            return history.RootElement.GetProperty("Content").GetString() == "external";
        }));
        var backup = new FullBackupService(appData, directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, backup.CreateOrUpdateUserBackup(entireFolder).Status);
        var archive = directory.File("conflict.notedbackup");
        Assert.IsTrue(backup.ExportUserBackup(archive).Success);
        var targetApp = directory.File("target-app");
        var targetNotes = directory.File("target-notes");
        Directory.CreateDirectory(targetApp);
        Directory.CreateDirectory(targetNotes);
        var target = new FullBackupService(targetApp, targetNotes);
        var imported = target.GetBackupImportPreview(archive);
        Assert.IsTrue(imported.IsValid);
        var logicalCopy = "notes/" + Path.GetFileName(preserved);
        Assert.AreEqual(openCopy || entireFolder, imported.Files.Any(file => file.LogicalPath == logicalCopy));
        Assert.AreEqual(FullBackupStatus.RestoreScheduled,
            target.ScheduleBackupImportRestore(archive, imported.BackupId, imported.VerificationToken!).Status);
        Assert.AreEqual(FullBackupStatus.Restored, target.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("editor edits", File.ReadAllText(Path.Combine(targetNotes, "note.md")));
        var restoredCopy = Path.Combine(targetNotes, Path.GetFileName(preserved));
        Assert.AreEqual(openCopy || entireFolder, File.Exists(restoredCopy));
        if (openCopy || entireFolder) Assert.AreEqual("external", File.ReadAllText(restoredCopy));
        Assert.AreEqual("external", File.ReadAllText(preserved));
    }

    [TestMethod]
    public void EmptySessionIncludesManagedDataAndArchivesButNotUnrelatedDocuments()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        Write(directory.File("app", "settings.json"), "{}");
        Write(directory.File("notes", "unrelated.json"), "unrelated");
        Write(directory.File("notes", "nested", "unrelated.txt"), "unrelated");
        Write(directory.File("notes", ".archive", "nested", "saved.md"), "archived");
        Write(directory.File("notes", ".archive", "unsupported.exe"), "excluded");
        WriteSession(directory);

        var entries = catalog.Collect(out var selected, out var external);

        CollectionAssert.AreEqual(new[] { "app/session/editor-workspace.json", "app/settings.json",
            "notes/.archive/nested/saved.md" }, entries.Select(entry => entry.LogicalPath).ToArray());
        Assert.AreEqual(0, selected.Count);
        Assert.AreEqual(0, external);
        Assert.AreEqual("unrelated", File.ReadAllText(directory.File("notes", "unrelated.json")));
    }

    [TestMethod]
    public void AllTabsAreCollectedAndAnOpenArchivedDocumentIsNotDuplicated()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var paths = new[] { directory.File("notes", "primary.txt"), directory.File("notes", "nested", "secondary.md"),
            directory.File("notes", "inactive.json"), directory.File("notes", ".archive", "saved.txt") };
        foreach (var path in paths) Write(path, "saved");
        WriteSession(directory, paths.Concat([paths[0].ToUpperInvariant()]).ToArray());

        var files = catalog.Collect(out var selected, out _);

        Assert.AreEqual(4, selected.Count);
        Assert.AreEqual(4, files.Count(file => file.LogicalPath.StartsWith("notes/")));
        Assert.AreEqual(1, files.Count(file => file.LogicalPath == "notes/.archive/saved.txt"));
    }

    [TestMethod]
    public void ExternalDocumentIsExcludedButItsSessionAndRecoveryAreRetained()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var external = directory.File("outside.txt");
        Write(external, "saved outside");
        WriteSession(directory, external);
        var recovery = directory.File("app", "recovery", NotePathIdentity.GetHash(Path.GetFullPath(external)) + ".json");
        Write(recovery, JsonSerializer.Serialize(new NoteRecoveryDraft(external, "unsaved outside", DateTime.UtcNow)));

        var files = catalog.Collect(out var selected, out var excluded);

        Assert.AreEqual(1, excluded);
        Assert.AreEqual(0, selected.Count);
        Assert.AreEqual(2, files.Count);
        Assert.IsTrue(files.Any(file => file.SourcePath == recovery));
        Assert.IsFalse(files.Any(file => file.SourcePath == external));
    }

    [TestMethod]
    public void SavedBytesAndUnsavedDraftAreCapturedSeparately()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var note = directory.File("notes", "one.txt");
        Write(note, "saved on disk");
        WriteSession(directory, note);
        var draft = directory.File("app", "recovery", NotePathIdentity.GetHash(Path.GetFullPath(note)) + ".json");
        Write(draft, JsonSerializer.Serialize(new NoteRecoveryDraft(note, "unsaved editor text", DateTime.UtcNow)));

        var files = catalog.Collect();

        Assert.AreEqual(13L, files.Single(file => file.LogicalPath == "notes/one.txt").Length);
        Assert.AreEqual(19L, files.Single(file => file.SourcePath == draft).ContentLength);
        Assert.AreEqual("saved on disk", File.ReadAllText(note));
    }

    [TestMethod]
    public void MissingSelectedDocumentFailsCollection()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        WriteSession(directory, directory.File("notes", "missing.txt"));
        Assert.ThrowsExactly<FileNotFoundException>(() => catalog.Collect());
    }

    [TestMethod]
    public void UnreadableSelectedDocumentFailsCollection()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var note = directory.File("notes", "locked.txt");
        Write(note, "saved");
        WriteSession(directory, note);
        using var locked = new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsExactly<IOException>(() => catalog.Collect());
    }

    [TestMethod]
    [DataRow("relative.txt")]
    [DataRow("")]
    [DataRow("C:\\unsupported.exe")]
    public void InvalidSessionDocumentCannotBecomeASilentExclusion(string path)
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        WriteSession(directory, path);
        Assert.ThrowsExactly<InvalidDataException>(() => catalog.Collect());
    }

    [TestMethod]
    public void ClosingDocumentsDoesNotLookLikeDataLoss()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var paths = Enumerable.Range(0, 5).Select(i => directory.File("notes", $"{i}.txt")).ToArray();
        foreach (var path in paths) Write(path, new string('x', 5000));
        WriteSession(directory, paths);
        var previous = catalog.Collect().Select(file => file.ToManifestEntry()).ToList();
        WriteSession(directory);
        var current = catalog.Collect(out var selected, out _);

        Assert.IsNull(FullBackupService.FindSuspiciousDataLoss(previous, current, selected));
        Assert.IsTrue(paths.All(File.Exists));
    }

    [TestMethod]
    public void ShrinkingSelectedDocumentsStillBlocksReplacement()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var note = directory.File("notes", "one.txt");
        Write(note, new string('x', 5000));
        WriteSession(directory, note);
        var previous = catalog.Collect().Select(file => file.ToManifestEntry()).ToList();
        Write(note, "small");
        var current = catalog.Collect(out var selected, out _);

        Assert.IsNotNull(FullBackupService.FindSuspiciousDataLoss(previous, current, selected));
    }

    [TestMethod]
    public void RemovingArchivedDocumentsStillTriggersDataLossProtection()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var paths = Enumerable.Range(0, 4).Select(i => directory.File("notes", ".archive", $"{i}.txt")).ToArray();
        foreach (var path in paths) Write(path, "archived");
        var previous = catalog.Collect().Select(file => file.ToManifestEntry()).ToList();
        foreach (var path in paths) File.Delete(path);
        var current = catalog.Collect(out var selected, out _);

        Assert.IsNotNull(FullBackupService.FindSuspiciousDataLoss(previous, current, selected));
    }

    [TestMethod]
    public void ClosedMiniPadsHistoryAndDeletedNotesRemainIncludedWithAnEmptySession()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var id = Guid.NewGuid();
        var unregistered = Guid.NewGuid();
        WriteSession(directory);
        Write(directory.File("app", "settings.json"), JsonSerializer.Serialize(new
        {
            MiniPadWindows = new Dictionary<Guid, object> { [id] = new { ReopenOnStartup = false } }
        }));
        Write(directory.File("app", "recovery", "minipads", $"{id:N}.json"),
            JsonSerializer.Serialize(new { Id = id, Content = "closed MiniPad" }));
        Write(directory.File("app", "recovery", "minipads", $"{unregistered:N}.json"),
            JsonSerializer.Serialize(new { Id = unregistered, Content = "unregistered" }));
        Write(directory.File("app", "note-history", "retained", "entry.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, NotePath = directory.File("notes", "closed.txt"),
            Content = "earlier saved text", SavedUtc = DateTime.UtcNow
        }));
        Write(directory.File("app", "DeletedNotes", "deleted.txt"), "deleted contents");

        var paths = catalog.Collect().Select(file => file.LogicalPath).ToArray();

        CollectionAssert.Contains(paths, $"app/recovery/minipads/{id:N}.json");
        CollectionAssert.DoesNotContain(paths, $"app/recovery/minipads/{unregistered:N}.json");
        CollectionAssert.Contains(paths, "app/note-history/retained/entry.json");
        CollectionAssert.Contains(paths, "app/DeletedNotes/deleted.txt");
    }

    [TestMethod]
    public void MissingPreviouslyCapturedSessionStillTriggersDataLossProtection()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        WriteSession(directory);
        var previous = catalog.Collect().Select(file => file.ToManifestEntry()).ToList();
        File.Delete(directory.File("app", "session", "editor-workspace.json"));
        var current = catalog.Collect(out var selected, out _);

        Assert.IsNotNull(FullBackupService.FindSuspiciousDataLoss(previous, current, selected));
    }

    [TestMethod]
    public void CorruptSessionFailsCollection()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        Write(directory.File("app", "session", "editor-workspace.json"), "{broken");

        Assert.ThrowsExactly<JsonException>(() => catalog.Collect());
    }

    [TestMethod]
    public void UnreadableSessionFailsCollection()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        WriteSession(directory);
        using var locked = new FileStream(directory.File("app", "session", "editor-workspace.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsExactly<IOException>(() => catalog.Collect());
    }

    [TestMethod]
    public void RestoreCaptureDistinguishesMissingFromUnreadableDestinations()
    {
        using var directory = new TemporaryTestDirectory();
        var catalog = CreateCatalog(directory);
        var note = directory.File("notes", "one.txt");
        Assert.IsNull(catalog.CaptureRestoreDestination("notes/one.txt", note, directory.File("notes")));
        Write(note, "current saved contents");
        Assert.IsNotNull(catalog.CaptureRestoreDestination("notes/one.txt", note, directory.File("notes")));
        using var locked = new FileStream(note, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsExactly<IOException>(() => catalog.CaptureRestoreDestination("notes/one.txt", note, directory.File("notes")));
    }

    private static BackupContentCatalog CreateCatalog(TemporaryTestDirectory directory)
    {
        Directory.CreateDirectory(directory.File("notes"));
        Directory.CreateDirectory(directory.File("app"));
        return new BackupContentCatalog(directory.File("app"), directory.File("notes"), directory.File("app", "full-snapshots"));
    }

    private static void WriteSession(TemporaryTestDirectory directory, params string[] paths) =>
        Write(directory.File("app", "session", "editor-workspace.json"), JsonSerializer.Serialize(new NoteEditorSession
        {
            Tabs = paths.Select(path => new NoteEditorTabState { FilePath = path }).ToList(),
            ActiveFilePath = paths.FirstOrDefault(),
            SecondaryFilePath = paths.Skip(1).FirstOrDefault()
        }));

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
