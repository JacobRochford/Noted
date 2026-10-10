using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileSystemLinkBackupReadTests
{
    [TestMethod]
    [DataRow("recovery")]
    [DataRow("note-history")]
    [DataRow("DeletedNotes")]
    [DataRow("notes/nested")]
    public void CollectionRejectsLinkedRootsAndDescendants(string relative)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "sentinel");
        var linkPath = relative.StartsWith("notes/") ? directory.File(relative) : Path.Combine(app, relative);
        using var link = new TestFileSystemLink(linkPath, outside);
        var service = new FullBackupService(app, notes);

        Assert.AreEqual(FullBackupStatus.Failed, service.CreateOrUpdateUserBackup().Status);
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.IsFalse(Directory.Exists(service.BackupDirectory));
    }

    [TestMethod]
    public void CollectionRejectsLinkedFixedFile()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("notes"));
        var sentinel = directory.File("sentinel.json");
        File.WriteAllText(sentinel, "{}");
        using var link = new TestFileSystemLink(directory.File("app", PersistencePaths.SettingsFile), sentinel, false);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Failed, service.CreateOrUpdateUserBackup().Status);
        Assert.AreEqual("{}", File.ReadAllText(sentinel));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SnapshotAndPreviewRejectSubstitutedLinkedManifestOrPayload(bool manifest)
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateBackup(directory);
        var preview = service.GetUserBackupPreview();
        Assert.IsTrue(preview.IsValid);
        var source = manifest
            ? Path.Combine(service.BackupDirectory, BackupFormat.UserDirectoryName, BackupFormat.ManifestFileName)
            : Path.Combine(service.BackupDirectory, BackupFormat.UserDirectoryName, "files", "notes", "one.txt");
        var sentinel = directory.File("sentinel");
        File.Copy(source, sentinel);
        var expected = File.ReadAllBytes(sentinel);
        File.Delete(source);
        using var link = new TestFileSystemLink(source, sentinel, false);

        Assert.IsFalse(service.GetUserBackupInfo().IsValid);
        var content = service.ReadUserBackupFile(preview.BackupId, preview.Files.Single());
        Assert.IsFalse(content.Success);
        Assert.IsTrue(content.NeedsRecheck);
        CollectionAssert.AreEqual(expected, File.ReadAllBytes(sentinel));
    }

    [TestMethod]
    public void ExportRejectsAliasIntoBackupStorageAndAcceptsExplicitExternalJunction()
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateBackup(directory);
        using var alias = new TestFileSystemLink(directory.File("alias"), service.BackupDirectory);
        var sentinel = Path.Combine(service.BackupDirectory, "sentinel.zip");
        File.WriteAllText(sentinel, "sentinel");
        Assert.IsFalse(service.ExportUserBackup(directory.File("alias", "sentinel.zip")).Success);
        Assert.AreEqual("sentinel", File.ReadAllText(sentinel));

        var external = directory.File("external");
        Directory.CreateDirectory(external);
        using var externalLink = new TestFileSystemLink(directory.File("export"), external);
        var result = service.ExportUserBackup(directory.File("export", "backup.zip"));
        Assert.IsTrue(result.Success, result.Message);
        var imported = service.GetBackupImportPreview(Path.Combine(external, "backup.zip"));
        Assert.IsTrue(imported.IsValid, imported.Error);
    }

    [TestMethod]
    public void LinkedConfiguredAppNotesRootStillExcludesBackupStorage()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "one.txt"), "saved");
        using var link = new TestFileSystemLink(directory.File("notes"), app);
        var service = new FullBackupService(app, directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);

        var files = new BackupContentCatalog(app, directory.File("notes"), service.BackupDirectory).Collect();

        Assert.AreEqual(1, files.Count);
        Assert.AreEqual("notes/one.txt", files.Single().LogicalPath);
    }

    [TestMethod]
    public void ExportRejectsShortNameAliasIntoBackupStorageWhenAvailable()
    {
        using var directory = new TemporaryTestDirectory();
        var service = CreateBackup(directory);
        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(service.BackupDirectory, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity ||
            string.Equals(buffer.ToString(), service.BackupDirectory, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("This filesystem does not provide a distinct Windows short-name alias.");
        var sentinel = Path.Combine(service.BackupDirectory, "sentinel.zip");
        File.WriteAllText(sentinel, "sentinel");

        Assert.IsFalse(service.ExportUserBackup(Path.Combine(buffer.ToString(), "sentinel.zip")).Success);

        Assert.AreEqual("sentinel", File.ReadAllText(sentinel));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, uint capacity);

    private static FullBackupService CreateBackup(TemporaryTestDirectory directory)
    {
        Directory.CreateDirectory(directory.File("notes"));
        File.WriteAllText(directory.File("notes", "one.txt"), "saved");
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        return service;
    }
}
