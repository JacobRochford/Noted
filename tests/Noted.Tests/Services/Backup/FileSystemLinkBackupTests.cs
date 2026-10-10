using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileSystemLinkBackupTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RestorePreflightRejectsLaterLinkedDestinationBeforeAnyWrite(bool imported)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "a.txt"), "saved a");
        Directory.CreateDirectory(Path.Combine(notes, "nested"));
        File.WriteAllText(Path.Combine(notes, "nested", "z.txt"), "saved z");
        var service = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        if (imported)
        {
            var archivePath = directory.File("export.zip");
            Assert.IsTrue(service.ExportUserBackup(archivePath).Success);
            var preview = service.GetBackupImportPreview(archivePath);
            Assert.IsTrue(preview.IsValid, preview.Error);
            Assert.AreEqual(FullBackupStatus.RestoreScheduled,
                service.ScheduleBackupImportRestore(archivePath, preview.BackupId, preview.VerificationToken!).Status);
        }
        else
            Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        File.WriteAllText(Path.Combine(notes, "a.txt"), "current a");
        Directory.Delete(Path.Combine(notes, "nested"), true);
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "z.txt"), "sentinel");
        using var link = new TestFileSystemLink(Path.Combine(notes, "nested"), outside);

        Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);

        Assert.AreEqual("current a", File.ReadAllText(Path.Combine(notes, "a.txt")));
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "z.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(service.BackupDirectory, BackupFormat.RestoreRequestFileName)));
        Assert.IsTrue(service.UserBackupExists);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void LinkedBackupStorageOrAncestorCannotDeleteOrStageExternalContent(bool ancestor)
    {
        using var directory = new TemporaryTestDirectory();
        var outside = directory.File("outside");
        var snapshotRoot = ancestor ? Path.Combine(outside, BackupFormat.BackupDirectoryName) : outside;
        var orphan = Path.Combine(snapshotRoot, BackupFormat.RestoredImportDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "sentinel.txt"), "sentinel");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "note.txt"), "note");
        var app = directory.File("app");
        using var link = new TestFileSystemLink(ancestor ? app : Path.Combine(app, BackupFormat.BackupDirectoryName), outside);

        var service = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Failed, service.CreateOrUpdateUserBackup().Status);
        Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(orphan, "sentinel.txt")));
        Assert.AreEqual(1, Directory.GetDirectories(snapshotRoot).Length);
    }

    [TestMethod]
    public void ConfiguredLinkedNotesRootSupportsBackupAndRestore()
    {
        using var directory = new TemporaryTestDirectory();
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "one.txt"), "saved");
        using var link = new TestFileSystemLink(directory.File("notes"), outside);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        File.WriteAllText(Path.Combine(outside, "one.txt"), "changed");
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("saved", File.ReadAllText(Path.Combine(outside, "one.txt")));
    }

    [TestMethod]
    public void RestoreRejectsLinkedDestinationFileAndRetainsRequest()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("notes"));
        var note = directory.File("notes", "one.txt");
        File.WriteAllText(note, "saved");
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, service.ScheduleUserBackupRestore().Status);
        File.Delete(note);
        var sentinel = directory.File("sentinel.txt");
        File.WriteAllText(sentinel, "sentinel");
        using var link = new TestFileSystemLink(note, sentinel, false);

        Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("sentinel", File.ReadAllText(sentinel));
        Assert.IsTrue(File.Exists(Path.Combine(service.BackupDirectory, BackupFormat.RestoreRequestFileName)));
    }

    [TestMethod]
    public void OrphanCleanupRejectsLinkedDescendantsAndPreservesTheFolder()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var orphan = Path.Combine(app, BackupFormat.BackupDirectoryName,
            BackupFormat.RestoredImportDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "sentinel");
        using var link = new TestFileSystemLink(Path.Combine(orphan, "nested"), outside);

        _ = new FullBackupService(app, directory.File("notes"));

        Assert.IsTrue(Directory.Exists(orphan));
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
    }

    [TestMethod]
    public void BeforeRestoreRetentionRejectsLinkedOldSnapshot()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("notes"));
        File.WriteAllText(directory.File("notes", "one.txt"), "note");
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "sentinel");
        for (var i = 1; i <= 3; i++)
            Directory.CreateDirectory(Path.Combine(service.BackupDirectory, BackupFormat.BeforeRestoreDirectoryPrefix + "2000010" + i));
        using var link = new TestFileSystemLink(
            Path.Combine(service.BackupDirectory, BackupFormat.BeforeRestoreDirectoryPrefix + "19990101"), outside);

        var result = service.CreateBeforeRestoreBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        Assert.IsNotNull(result.Warning);
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
    }

    [TestMethod]
    public void ImportCannotStageThroughLinkedBackupStorage()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("notes"));
        File.WriteAllText(directory.File("notes", "one.txt"), "saved");
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var archivePath = directory.File("backup.zip");
        Assert.IsTrue(service.ExportUserBackup(archivePath).Success);
        var preview = service.GetBackupImportPreview(archivePath);
        Assert.IsTrue(preview.IsValid);
        var outside = directory.File("outside");
        Directory.Move(service.BackupDirectory, outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "sentinel");
        var before = Directory.GetFileSystemEntries(outside).Length;
        using var link = new TestFileSystemLink(service.BackupDirectory, outside);

        Assert.AreEqual(FullBackupStatus.Failed,
            service.ScheduleBackupImportRestore(archivePath, preview.BackupId, preview.VerificationToken!).Status);

        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.AreEqual(before, Directory.GetFileSystemEntries(outside).Length);
        Assert.IsFalse(File.Exists(Path.Combine(outside, BackupFormat.RestoreRequestFileName)));
    }
}
