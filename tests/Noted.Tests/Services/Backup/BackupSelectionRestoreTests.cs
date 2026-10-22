using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

public sealed partial class FullBackupServiceTests
{
    [TestMethod]
    public void MissingSelectedDocumentLeavesExistingBackupUnchanged()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var first = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, first.CreateOrUpdateUserBackup().Status);
        var manifestPath = Path.Combine(first.BackupDirectory, "protected", "manifest.json");
        var originalManifest = File.ReadAllBytes(manifestPath);
        File.Delete(Path.Combine(paths.Notes, "one.txt"));

        var result = new FullBackupService(paths.AppData, paths.Notes).CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifestPath));
        Assert.AreEqual("note one", File.ReadAllText(Path.Combine(first.BackupDirectory, "protected", "files", "notes", "one.txt")));
    }

    [TestMethod]
    public void LosingTheOnlySessionFileBlocksUpdateInsteadOfReportingNoData()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        BackupTestSession.Save(app);
        var first = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, first.CreateOrUpdateUserBackup().Status);
        var manifestPath = Path.Combine(first.BackupDirectory, "protected", "manifest.json");
        var originalManifest = File.ReadAllBytes(manifestPath);
        File.Delete(Path.Combine(app, "session", "editor-workspace.json"));

        var result = new FullBackupService(app, notes).CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifestPath));
    }

    [TestMethod]
    public void ClosingTheOnlySelectedDocumentAllowsScopeReduction()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var note = Path.Combine(paths.Notes, "one.txt");
        var saved = new string('x', 5000);
        File.WriteAllText(note, saved);
        Assert.AreEqual(FullBackupStatus.Created,
            new FullBackupService(paths.AppData, paths.Notes).CreateOrUpdateUserBackup().Status);
        BackupTestSession.Save(paths.AppData);
        var service = new FullBackupService(paths.AppData, paths.Notes);

        var result = service.CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status, result.Message);
        Assert.IsFalse(service.GetUserBackupPreview().Files.Any(file => file.LogicalPath == "notes/one.txt"));
        Assert.AreEqual(saved, File.ReadAllText(note));
        Assert.AreEqual(saved, File.ReadAllText(Path.Combine(service.BackupDirectory, "previous", "files", "notes", "one.txt")));
    }

    [TestMethod]
    public void PreparationSelectsTheFreshlySavedSession()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var freshNote = Path.Combine(paths.Notes, "fresh.txt");
        File.WriteAllText(freshNote, "fresh document");
        var service = new FullBackupService(paths.AppData, paths.Notes);
        var coordinator = new BackupCoordinator(service, new ShutdownFlushCoordinator(), () => paths.Notes);
        using var editor = coordinator.RegisterPreparationParticipant("Note editor", BackupPreparationPhase.AfterSharedFlush,
            () => { BackupTestSession.Save(paths.AppData, freshNote); return PersistenceSaveResult.Succeeded(); }, () => null);

        Assert.AreEqual(FullBackupStatus.Created, coordinator.CreateOrUpdateUserBackup().Status);

        var files = service.GetUserBackupPreview().Files;
        Assert.IsTrue(files.Any(file => file.LogicalPath == "notes/fresh.txt"));
        Assert.IsFalse(files.Any(file => file.LogicalPath == "notes/one.txt"));
    }

}
