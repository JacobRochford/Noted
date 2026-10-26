using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class EntireNotesFolderBackupTests
{
    [TestMethod]
    public void PreferenceDefaultsOffAndPersistsWithoutChangingExistingSnapshots()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.App, "settings.json"), "{\"SchemaVersion\":1}");
        var settings = new AppSettingsService(fixture.App);
        Assert.IsFalse(settings.LoadIncludeEntireNotesFolderInBackups());
        Assert.IsFalse(JsonSerializer.Deserialize<AppSettingsDocument>("{}")!.IncludeEntireNotesFolderInBackups);
        fixture.Create(false);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        settings.SaveIncludeEntireNotesFolderInBackups(true);
        Assert.IsTrue(new AppSettingsService(fixture.App).LoadIncludeEntireNotesFolderInBackups());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
        settings.SaveIncludeEntireNotesFolderInBackups(false);
        Assert.IsFalse(new AppSettingsService(fixture.App).LoadIncludeEntireNotesFolderInBackups());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CollectionRespectsScopeAndDeduplicatesArchive(bool entire)
    {
        using var fixture = new Fixture();
        fixture.Write("closed.md", "closed");
        fixture.Write("nested/hidden.bin", "binary");
        File.SetAttributes(Path.Combine(fixture.Notes, "nested", "hidden.bin"), FileAttributes.Hidden);
        fixture.Write(".archive/archived.txt", "archive");
        BackupTestSession.Save(fixture.App, Path.Combine(fixture.Notes, "one.txt"), Path.Combine(fixture.Notes, ".archive", "archived.txt"));
        fixture.Create(entire);
        var preview = fixture.Service.GetUserBackupPreview();
        Assert.IsTrue(preview.IsValid);
        Assert.AreEqual(entire, preview.Files.Any(file => file.LogicalPath == "notes/closed.md"));
        Assert.AreEqual(entire, preview.Files.Any(file => file.LogicalPath == "notes/nested/hidden.bin"));
        Assert.AreEqual(1, preview.Files.Count(file => file.LogicalPath == "notes/.archive/archived.txt"));
        Assert.IsTrue(preview.Files.Any(file => file.LogicalPath == "app/session/editor-workspace.json"));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public void ScopeTransitionsKeepIntendedFiles(bool initial, bool next)
    {
        using var fixture = new Fixture();
        fixture.Write("closed.txt", new string('c', 8000));
        fixture.Write(".archive/closed.bin", new string('b', 8000));
        fixture.Create(initial);
        fixture.Write("one.txt", "changed open content");
        fixture.Create(next);
        Assert.AreEqual(next, fixture.Service.GetUserBackupPreview().Files.Any(file => file.LogicalPath == "notes/closed.txt"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingSelectedDocumentNeverBecomesAnExclusion(bool entire)
    {
        using var fixture = new Fixture();
        fixture.Create(false);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        File.Delete(Path.Combine(fixture.Notes, "one.txt"));
        Assert.AreEqual(FullBackupStatus.Failed, fixture.Service.CreateOrUpdateUserBackup(entire).Status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
    }

    [TestMethod]
    public void EntireModeDetectsMissingPreviouslyClosedFileAndNarrowingAllowsExclusion()
    {
        using var fixture = new Fixture();
        fixture.Write("closed.bin", "binary");
        fixture.Create(true);
        File.Delete(Path.Combine(fixture.Notes, "closed.bin"));
        Assert.AreEqual(FullBackupStatus.Blocked, fixture.Service.CreateOrUpdateUserBackup(true).Status);
        fixture.Create(false);
    }

    [TestMethod]
    public void BinaryFilesDoNotMaskManagedContentLoss()
    {
        using var fixture = new Fixture();
        fixture.Write("large.bin", new string('b', 100_000));
        fixture.Write("one.txt", new string('n', 8000));
        fixture.Create(true);
        fixture.Write("one.txt", "tiny");
        fixture.Write("new.bin", new string('b', 100_000));
        Assert.AreEqual(FullBackupStatus.Blocked, fixture.Service.CreateOrUpdateUserBackup(true).Status);
    }

    [TestMethod]
    public void LocalOnlyConfirmationCanCancelBeforeReplacementAndRetry()
    {
        using var fixture = new Fixture();
        fixture.Create(false);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        fixture.Write("image.png", "binary");
        var service = fixture.Service;
        var confirmations = 0;
        var result = service.CreateOrUpdateUserBackup(true, reason =>
        {
            confirmations++;
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
            return false;
        });
        Assert.AreEqual(FullBackupStatus.Skipped, result.Status);
        Assert.AreEqual(1, confirmations);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup(true, _ => true).Status);
        var info = service.GetUserBackupInfo();
        Assert.IsTrue(info.IsValid);
        Assert.IsNotNull(info.ExportIneligibility);
        Assert.IsFalse(service.ExportUserBackup(fixture.Directory.File("export.notedbackup")).Success);
    }

    [TestMethod]
    [DataRow("add")]
    [DataRow("delete")]
    [DataRow("change")]
    public void SourceChangesBeforePublicationKeepExistingBackup(string change)
    {
        using var fixture = new Fixture();
        fixture.Create(false);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        fixture.Write("closed.bin", "binary");
        var result = fixture.Service.CreateOrUpdateUserBackup(true, _ =>
        {
            if (change == "add") fixture.Write("late.txt", "late");
            if (change == "delete") File.Delete(Path.Combine(fixture.Notes, "closed.bin"));
            if (change == "change") fixture.Write("one.txt", "changed");
            return true;
        });
        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
        Assert.IsTrue(fixture.Service.GetUserBackupInfo().IsValid);
    }

    [TestMethod]
    public void LockedFilePreventsReplacement()
    {
        using var fixture = new Fixture();
        fixture.Create(false);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        fixture.Write("closed.bin", "binary");
        using var locked = new FileStream(Path.Combine(fixture.Notes, "closed.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.AreEqual(FullBackupStatus.Failed, fixture.Service.CreateOrUpdateUserBackup(true).Status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
    }

    [TestMethod]
    public void ExplicitRootAliasWorksButLinkedDescendantFails()
    {
        using var fixture = new Fixture();
        var alias = fixture.Directory.File("alias");
        using var rootLink = new TestFileSystemLink(alias, fixture.Notes);
        BackupTestSession.Save(fixture.App, Path.Combine(alias, "one.txt"));
        var service = new FullBackupService(fixture.App, alias);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup(true).Status);
        using var descendant = new TestFileSystemLink(Path.Combine(fixture.Notes, "nested"), fixture.App);
        Assert.AreEqual(FullBackupStatus.Failed, new FullBackupService(fixture.App, alias).CreateOrUpdateUserBackup(true).Status);
    }

    [TestMethod]
    public void MissingSelectedDocumentUnderAnotherRootAliasStillFails()
    {
        using var fixture = new Fixture();
        var alias = fixture.Directory.File("alias");
        using var rootLink = new TestFileSystemLink(alias, fixture.Notes);
        File.Delete(Path.Combine(fixture.Notes, "one.txt"));
        Assert.AreEqual(FullBackupStatus.Failed,
            new FullBackupService(fixture.App, alias).CreateOrUpdateUserBackup(true).Status);
    }

    [TestMethod]
    public void LinkedFileIsRejectedWithoutChangingTheExistingBackup()
    {
        using var fixture = new Fixture();
        fixture.Create(false);
        var before = File.ReadAllBytes(fixture.ManifestPath);
        using var link = new TestFileSystemLink(Path.Combine(fixture.Notes, "linked.bin"),
            Path.Combine(fixture.Notes, "one.txt"), isDirectory: false);
        Assert.AreEqual(FullBackupStatus.Failed, fixture.Service.CreateOrUpdateUserBackup(true).Status);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.ManifestPath));
    }

    [TestMethod]
    public void EqualAppAndNotesRootsRemainAllowedOnlyInSelectedMode()
    {
        using var fixture = new Fixture();
        BackupTestSession.Save(fixture.App);
        var service = new FullBackupService(fixture.App, fixture.App);
        var result = service.CreateOrUpdateUserBackup(true);
        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "application-data");
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NotesRootContainingAppDataIsBlockedEvenThroughAlias(bool alias)
    {
        using var fixture = new Fixture();
        var root = fixture.Directory.Path;
        using var link = alias ? new TestFileSystemLink(fixture.Directory.File("alias"), root) : null;
        var service = new FullBackupService(fixture.App, alias ? fixture.Directory.File("alias") : root);
        var result = service.CreateOrUpdateUserBackup(true);
        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "application-data");
    }

    [TestMethod]
    public void RecentBackupsUsePreferenceAndExposeLocalOnlyWithoutWarning()
    {
        using var fixture = new Fixture();
        fixture.Create(false);
        fixture.Write("closed.bin", "binary");
        var coordinator = new BackupCoordinator(fixture.Service, new ShutdownFlushCoordinator(), () => fixture.Notes, () => true);
        var result = coordinator.TryUpdateRecentBackup(TimeSpan.FromTicks(1));
        Assert.IsNotNull(result);
        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        Assert.IsNull(result.Warning);
        Assert.IsNotNull(result.ExportIneligibility);
        Assert.IsNotNull(coordinator.GetUserBackupInfo().RecentBackupNotice);
    }

    [TestMethod]
    public void EntireBackupRestoresBinaryFilesProtectsOverwritesAndKeepsUnrelatedFiles()
    {
        using var fixture = new Fixture();
        fixture.Write("closed.bin", "original binary");
        fixture.Create(true);
        fixture.Write("closed.bin", "current binary");
        fixture.Write("unrelated.bin", "keep this");
        var service = fixture.Service;
        var prepared = service.PrepareBackupRestore(null, null, null);
        Assert.IsTrue(prepared.Success);
        var snapshot = service.GetBeforeRestoreBackups().Backups.Single();
        var protectedFile = snapshot.Preview.Files.Single(file => file.LogicalPath == "notes/closed.bin");
        CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("current binary"),
            service.ReadBeforeRestoreBackupFile(snapshot, protectedFile).Data);
        Assert.IsFalse(snapshot.Preview.Files.Any(file => file.LogicalPath == "notes/unrelated.bin"));
        Assert.IsTrue(service.ExportBeforeRestoreBackupFile(snapshot, protectedFile,
            fixture.Directory.File("protected.bin")).Success);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("original binary", File.ReadAllText(Path.Combine(fixture.Notes, "closed.bin")));
        Assert.AreEqual("keep this", File.ReadAllText(Path.Combine(fixture.Notes, "unrelated.bin")));
    }

    [TestMethod]
    public void DuplicateManagedAndNotesSourceIsRejected()
    {
        using var fixture = new Fixture();
        var notes = Path.Combine(fixture.App, "DeletedNotes");
        System.IO.Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "one.txt"), "shared source");
        BackupTestSession.Save(fixture.App, Path.Combine(notes, "one.txt"));
        var result = new FullBackupService(fixture.App, notes).CreateOrUpdateUserBackup(true);
        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "Multiple backup entries");
    }

    [TestMethod]
    public void NotesInsideBackupStorageAreRejected()
    {
        using var fixture = new Fixture();
        var notes = Path.Combine(fixture.Service.BackupDirectory, "notes");
        System.IO.Directory.CreateDirectory(notes);
        var result = new FullBackupService(fixture.App, notes).CreateOrUpdateUserBackup(true);
        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        StringAssert.Contains(result.Message, "backup storage");
    }

    [TestMethod]
    public void EntireFolderContainingOnlySupportedFilesRemainsPortable()
    {
        using var fixture = new Fixture();
        fixture.Write("nested/closed.md", "closed note");
        fixture.Create(true);
        Assert.IsNull(fixture.Service.GetUserBackupInfo().ExportIneligibility);
        var archive = fixture.Directory.File("export.notedbackup");
        Assert.IsTrue(fixture.Service.ExportUserBackup(archive).Success);
        Assert.IsTrue(fixture.Service.GetBackupImportPreview(archive).IsValid);
    }

    private sealed class Fixture : IDisposable
    {
        internal TemporaryTestDirectory Directory { get; } = new();
        internal string App => Directory.File("app");
        internal string Notes => Directory.File("notes");
        internal FullBackupService Service => new(App, Notes);
        internal string ManifestPath => Path.Combine(App, "full-snapshots", "protected", "manifest.json");

        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(App);
            Write("one.txt", "open content");
            BackupTestSession.Save(App, Path.Combine(Notes, "one.txt"));
        }

        internal void Write(string relative, string contents)
        {
            var path = Path.Combine(Notes, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        internal void Create(bool entire)
        {
            var result = Service.CreateOrUpdateUserBackup(entire, _ => true);
            Assert.AreEqual(FullBackupStatus.Created, result.Status, result.Message);
        }

        public void Dispose() => Directory.Dispose();
    }
}
