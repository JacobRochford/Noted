using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

public sealed partial class FullBackupServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RestorePreparationProtectsClosedHistoricalDocumentsAtTheirActualRoot(bool changedRoot)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var oldNotes = directory.File("old-notes");
        var currentNotes = changedRoot ? directory.File("current-notes") : oldNotes;
        Directory.CreateDirectory(oldNotes);
        Directory.CreateDirectory(currentNotes);
        WriteHistoricalUserBackup(app, oldNotes);
        BackupTestSession.Save(app);
        var destination = Path.Combine(oldNotes, "closed.txt");
        File.WriteAllText(destination, "current contents to protect");
        if (changedRoot) File.WriteAllText(Path.Combine(currentNotes, "closed.txt"), "unrelated current root");
        var service = new FullBackupService(app, currentNotes);

        var prepared = service.PrepareBackupRestore(null, null, null);

        Assert.IsTrue(prepared.Success, prepared.ProtectionResult.Message);
        Assert.AreEqual(1, Directory.GetDirectories(service.BackupDirectory, "before-restore-*").Length);
        var protection = prepared.ProtectionResult.BackupPath!;
        Assert.AreEqual("current contents to protect", File.ReadAllText(Path.Combine(protection, "files", "notes", "closed.txt")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(protection, "manifest.json")));
        Assert.AreEqual(oldNotes, manifest.RootElement.GetProperty("NotesRoot").GetString());
        Assert.AreEqual("current contents to protect", File.ReadAllText(destination));
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("historical saved text", File.ReadAllText(destination));
        if (changedRoot) Assert.AreEqual("unrelated current root", File.ReadAllText(Path.Combine(currentNotes, "closed.txt")));
    }

    [TestMethod]
    public void ImportedRestoreProtectsRebasedClosedDestinations()
    {
        using var directory = new TemporaryTestDirectory();
        var sourceApp = directory.File("source-app");
        var sourceNotes = directory.File("source-notes");
        Directory.CreateDirectory(sourceNotes);
        WriteHistoricalUserBackup(sourceApp, sourceNotes);
        var source = new FullBackupService(sourceApp, sourceNotes);
        var archive = directory.File("historical.notedbackup");
        Assert.IsTrue(source.ExportUserBackup(archive).Success);
        var targetApp = directory.File("target-app");
        var targetNotes = directory.File("target-notes");
        Directory.CreateDirectory(targetNotes);
        BackupTestSession.Save(targetApp);
        File.WriteAllText(Path.Combine(targetNotes, "closed.txt"), "local text");
        var target = new FullBackupService(targetApp, targetNotes);
        var preview = target.GetBackupImportPreview(archive);
        Assert.IsTrue(preview.IsValid, preview.Error);

        var prepared = target.PrepareBackupRestore(archive, preview.BackupId, preview.VerificationToken);

        Assert.IsTrue(prepared.Success, prepared.ProtectionResult.Message);
        Assert.AreEqual("local text", File.ReadAllText(Path.Combine(prepared.ProtectionResult.BackupPath!, "files", "notes", "closed.txt")));
        Assert.AreEqual("local text", File.ReadAllText(Path.Combine(targetNotes, "closed.txt")));
        Assert.AreEqual(FullBackupStatus.Restored, target.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("historical saved text", File.ReadAllText(Path.Combine(targetNotes, "closed.txt")));
    }

    [TestMethod]
    public void UnreadableClosedRestoreDestinationStopsScheduling()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        WriteHistoricalUserBackup(app, notes);
        BackupTestSession.Save(app);
        var destination = Path.Combine(notes, "closed.txt");
        File.WriteAllText(destination, "current text");
        var service = new FullBackupService(app, notes);
        using var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var prepared = service.PrepareBackupRestore(null, null, null);

        Assert.IsFalse(prepared.Success);
        Assert.IsNull(prepared.ScheduleResult);
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
        Assert.AreEqual(0, Directory.GetDirectories(service.BackupDirectory, "before-restore-*").Length);
    }

    [TestMethod]
    public void ChangedDestinationsCannotBeScheduledUnderThePreviouslyProtectedBackupId()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        WriteHistoricalUserBackup(app, notes);
        var service = new FullBackupService(app, notes);
        var manifestPath = Path.Combine(service.BackupDirectory, "protected", "manifest.json");
        var original = JsonSerializer.Deserialize<FullBackupManifest>(
            File.ReadAllBytes(manifestPath), BackupFormat.JsonOptions)!;
        var otherNotes = directory.File("other-notes");
        Directory.CreateDirectory(otherNotes);
        var changed = original with { NotesRoot = otherNotes,
            Entries = original.Entries.Select(entry => entry with
            {
                OriginalPath = Path.Combine(otherNotes, "closed.txt")
            }).ToList() };
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(changed, BackupFormat.JsonOptions));

        var result = service.ScheduleUserBackupRestore(original);

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

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

    private static void WriteHistoricalUserBackup(string app, string notes)
    {
        var root = Path.Combine(app, "full-snapshots", "protected");
        var bytes = Encoding.UTF8.GetBytes("historical saved text");
        Directory.CreateDirectory(Path.Combine(root, "files", "notes"));
        File.WriteAllBytes(Path.Combine(root, "files", "notes", "closed.txt"), bytes);
        // literal historical fields keep this fixture independent of the current collector.
        WriteJson(Path.Combine(root, "manifest.json"), new
        {
            SchemaVersion = 1, SnapshotId = Guid.NewGuid(), Role = "Protected", CreatedUtc = DateTime.UtcNow,
            AppDataRoot = app, NotesRoot = notes,
            Entries = new[] { new { LogicalPath = "notes/closed.txt", OriginalPath = Path.Combine(notes, "closed.txt"),
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ContentLength = bytes.Length } }
        });
    }
}
