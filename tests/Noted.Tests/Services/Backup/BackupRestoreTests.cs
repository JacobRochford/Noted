using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupRestoreTests
{
    [TestMethod]
    public void ApplyPendingRestoreWritesVerifiedFilesAndRemovesTheRequest()
    {
        using var directory = new TemporaryTestDirectory();
        var fixture = CreateFixture(directory, "backup content");
        File.WriteAllText(fixture.NotePath, "current content");
        WriteRestoreRequest(fixture.BackupDirectory, fixture.BackupId);

        var result = fixture.Restore.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Restored, result.Status);
        Assert.AreEqual("backup content", File.ReadAllText(fixture.NotePath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    public void ApplyPendingRestoreLeavesDataAndRequestWhenTheBackupIdChanged()
    {
        using var directory = new TemporaryTestDirectory();
        var fixture = CreateFixture(directory, "backup content");
        File.WriteAllText(fixture.NotePath, "current content");
        var requestPath = WriteRestoreRequest(fixture.BackupDirectory, Guid.NewGuid());

        var result = fixture.Restore.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        Assert.AreEqual("current content", File.ReadAllText(fixture.NotePath));
        Assert.IsTrue(File.Exists(requestPath));
    }

    [TestMethod]
    public void ApplyPendingRestoreSkipsWhenNoRequestExists()
    {
        using var directory = new TemporaryTestDirectory();
        var fixture = CreateFixture(directory, "backup content");

        var result = fixture.Restore.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Skipped, result.Status);
        Assert.AreEqual("No backup restore is pending.", result.Message);
    }

    private static RestoreFixture CreateFixture(
        TemporaryTestDirectory directory,
        string backupContent)
    {
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        var backupDirectory = Path.Combine(appData, "full-snapshots");
        var protectedDirectory = Path.Combine(backupDirectory, "protected");
        var storedNotePath = Path.Combine(protectedDirectory, "files", "notes", "one.txt");
        var notePath = Path.Combine(notes, "one.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(storedNotePath)!);
        Directory.CreateDirectory(notes);
        File.WriteAllText(storedNotePath, backupContent);
        var data = Encoding.UTF8.GetBytes(backupContent);
        var backupId = Guid.NewGuid();
        JsonFileStore.Write(
            Path.Combine(protectedDirectory, BackupFormat.ManifestFileName),
            new FullBackupManifest(
                BackupFormat.BackupSchemaVersion,
                backupId,
                FullBackupType.User,
                DateTime.UtcNow,
                appData,
                notes,
                [
                    new FullBackupEntry(
                        "notes/one.txt",
                        notePath,
                        data.LongLength,
                        Convert.ToHexString(SHA256.HashData(data)),
                        null,
                        data.LongLength)
                ]),
            options: BackupFormat.JsonOptions);

        string GetDestination(string logicalPath, FullBackupManifest manifest) =>
            logicalPath.StartsWith("notes/", StringComparison.OrdinalIgnoreCase)
                ? BackupFormat.GetContainedPath(manifest.NotesRoot, logicalPath["notes/".Length..])
                : BackupFormat.GetContainedPath(manifest.AppDataRoot, logicalPath["app/".Length..]);

        var reader = new BackupArchiveReader(
            GetDestination,
            (entry, bytes, _) => new ImportedBackupFile(
                entry.LogicalPath,
                entry.LogicalPath,
                bytes,
                entry.ItemCount,
                entry.ContentLength),
            (manifest, id, _) => manifest with { BackupId = id });
        var restore = new BackupRestore(
            backupDirectory,
            reader,
            GetDestination,
            path => Directory.Delete(path, recursive: true));
        return new RestoreFixture(backupDirectory, notePath, backupId, restore);
    }

    private static string WriteRestoreRequest(string backupDirectory, Guid backupId)
    {
        var path = Path.Combine(backupDirectory, "restore-request.json");
        JsonFileStore.Write(
            path,
            new FullBackupRestoreRequest(
                BackupFormat.BackupSchemaVersion,
                backupId,
                DateTime.UtcNow),
            options: BackupFormat.JsonOptions);
        return path;
    }

    private sealed record RestoreFixture(
        string BackupDirectory,
        string NotePath,
        Guid BackupId,
        BackupRestore Restore);
}
