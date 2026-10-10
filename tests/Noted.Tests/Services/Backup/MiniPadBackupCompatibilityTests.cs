using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class MiniPadBackupCompatibilityTests
{
    [TestMethod]
    [DataRow("micro-scratchpads", false)]
    [DataRow("minipads", false)]
    [DataRow("micro-scratchpads", true)]
    [DataRow("minipads", true)]
    public void DirectAndPreviouslyStagedRestoresUseCanonicalDestination(string folder, bool staged)
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var service = new FullBackupService(appData, directory.File("notes"));
        var id = Guid.NewGuid();
        var importId = staged ? Guid.NewGuid() : (Guid?)null;
        var snapshotPath = Path.Combine(service.BackupDirectory, staged ? $"pending-import-{importId:N}" : "protected");
        var logicalPath = $"app/recovery/{folder}/{id:N}.json";
        var bytes = DraftBytes(id, "historical content");
        var manifest = WriteSnapshot(snapshotPath, appData, directory.File("notes"), new() { [logicalPath] = bytes });
        var originalManifest = File.ReadAllBytes(Path.Combine(snapshotPath, "manifest.json"));
        WriteRequest(service.BackupDirectory, manifest.BackupId, importId);
        new MiniPadRecoveryService(appData, id).SaveDraft("current content");
        if (!staged)
        {
            var preview = service.GetUserBackupPreview();
            Assert.IsTrue(preview.IsValid, preview.Error);
            Assert.AreEqual("MiniPad", preview.Files.Single().Category);
            var content = service.ReadUserBackupFile(preview.BackupId, preview.Files.Single());
            Assert.IsTrue(content.Success, content.Error);
            CollectionAssert.AreEqual(bytes, content.Data!);
        }

        var result = service.ApplyPendingUserBackupRestore();
        MiniPadRecoveryMigration.Migrate(appData);

        Assert.AreEqual(FullBackupStatus.Restored, result.Status, result.Message);
        Assert.AreEqual("historical content", new MiniPadRecoveryService(appData, id).LoadDraft().Draft!.Content);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(appData, "recovery", "minipads", $"{id:N}.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(appData, "recovery", "micro-scratchpads")));
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
        if (!staged)
        {
            CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(Path.Combine(snapshotPath, "manifest.json")));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(BackupFormat.GetContainedPath(Path.Combine(snapshotPath, "files"), logicalPath)));
        }
    }

    [TestMethod]
    [DataRow("micro-scratchpads")]
    [DataRow("minipads")]
    public void ImportedPreviewUsesCanonicalPathAndRetainsSourceLookup(string folder)
    {
        using var directory = new TemporaryTestDirectory();
        var id = Guid.NewGuid();
        var sourcePath = $"app/recovery/{folder}/{id:N}.json";
        var bytes = DraftBytes(id, "imported content");
        var snapshot = directory.File("historical-snapshot");
        WriteSnapshot(snapshot, directory.File("old-app"), directory.File("old-notes"), new() { [sourcePath] = bytes });
        var archivePath = directory.File("historical.notedbackup");
        ZipFile.CreateFromDirectory(snapshot, archivePath);
        var archiveBytes = File.ReadAllBytes(archivePath);
        var appData = directory.File("new-app");
        var service = new FullBackupService(appData, directory.File("new-notes"));

        var preview = service.GetBackupImportPreview(archivePath);

        Assert.IsTrue(preview.IsValid, preview.Error);
        var file = preview.Files.Single();
        Assert.AreEqual($"app/recovery/minipads/{id:N}.json", file.LogicalPath);
        Assert.AreEqual($"recovery/minipads/{id:N}.json", file.DisplayPath);
        Assert.AreEqual(sourcePath, file.SourceLogicalPath);
        Assert.AreEqual("MiniPad", file.Category);
        var content = service.ReadBackupImportFile(archivePath, preview.BackupId, preview.VerificationToken!, file);
        Assert.IsTrue(content.Success, content.Error);
        CollectionAssert.AreEqual(bytes, content.Data!);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled,
            service.ScheduleBackupImportRestore(archivePath, preview.BackupId, preview.VerificationToken!).Status);
        Assert.AreEqual(FullBackupStatus.Restored, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("imported content", new MiniPadRecoveryService(appData, id).LoadDraft().Draft!.Content);
        CollectionAssert.AreEqual(archiveBytes, File.ReadAllBytes(archivePath));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void DirectAndStagedAliasCollisionsFailBeforeAnyRestoreWrites(bool staged, bool identical)
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var service = new FullBackupService(appData, directory.File("notes"));
        var id = Guid.NewGuid();
        var importId = staged ? Guid.NewGuid() : (Guid?)null;
        var snapshot = Path.Combine(service.BackupDirectory, staged ? $"pending-import-{importId:N}" : "protected");
        var manifest = WriteSnapshot(snapshot, appData, directory.File("notes"), CollisionFiles(id, identical));
        WriteRequest(service.BackupDirectory, manifest.BackupId, importId);
        var checklist = Path.Combine(appData, "checklist.json");
        File.WriteAllText(checklist, "current checklist");
        new MiniPadRecoveryService(appData, id).SaveDraft("current draft");

        var result = service.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        Assert.AreEqual("current checklist", File.ReadAllText(checklist));
        Assert.AreEqual("current draft", new MiniPadRecoveryService(appData, id).LoadDraft().Draft!.Content);
        Assert.IsTrue(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
        Assert.IsTrue(Directory.Exists(snapshot));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ImportAliasCollisionsFailBeforeStagingOrRestoreWrites(bool identical)
    {
        using var directory = new TemporaryTestDirectory();
        var snapshot = directory.File("historical-snapshot");
        var manifest = WriteSnapshot(snapshot, directory.File("old-app"), directory.File("old-notes"), CollisionFiles(Guid.NewGuid(), identical));
        var archivePath = directory.File("collision.notedbackup");
        ZipFile.CreateFromDirectory(snapshot, archivePath);
        var service = new FullBackupService(directory.File("new-app"), directory.File("new-notes"));
        var token = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(snapshot, "manifest.json"))));

        Assert.IsFalse(service.GetBackupImportPreview(archivePath).IsValid);
        var result = service.ScheduleBackupImportRestore(archivePath, manifest.BackupId, token);

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        Assert.IsFalse(Directory.Exists(service.BackupDirectory));
        Assert.IsFalse(Directory.Exists(directory.File("new-app", "recovery")));
    }

    [TestMethod]
    [DataRow("micro-scratchpads", "invalid-json")]
    [DataRow("minipads", "invalid-json")]
    [DataRow("micro-scratchpads", "wrong-id")]
    [DataRow("minipads", "wrong-id")]
    [DataRow("micro-scratchpads", "nested")]
    [DataRow("minipads", "nested")]
    [DataRow("micro-scratchpads", "traversal")]
    [DataRow("minipads", "traversal")]
    public void ImportRetainsJsonIdentityAndPathValidation(string folder, string problem)
    {
        using var directory = new TemporaryTestDirectory();
        var id = Guid.NewGuid();
        var logicalPath = $"app/recovery/{folder}/{id:N}.json";
        var bytes = problem == "invalid-json" ? Encoding.UTF8.GetBytes("{broken") : DraftBytes(problem == "wrong-id" ? Guid.NewGuid() : id, "content");
        if (problem == "nested")
            logicalPath = $"app/recovery/{folder}/nested/{id:N}.json";
        var snapshot = directory.File("historical-snapshot");
        WriteSnapshot(snapshot, directory.File("old-app"), directory.File("old-notes"), new() { [logicalPath] = bytes });
        var archivePath = directory.File("invalid.notedbackup");
        ZipFile.CreateFromDirectory(snapshot, archivePath);
        if (problem == "traversal")
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
            using var stream = archive.CreateEntry($"files/app/recovery/{folder}/../escape.json").Open();
            stream.Write(bytes);
        }
        var service = new FullBackupService(directory.File("new-app"), directory.File("new-notes"));

        Assert.IsFalse(service.GetBackupImportPreview(archivePath).IsValid);
    }

    private static Dictionary<string, byte[]> CollisionFiles(Guid id, bool identical)
    {
        var bytes = DraftBytes(id, "legacy");
        return new()
        {
            ["app/checklist.json"] = Encoding.UTF8.GetBytes("[]"),
            [$"app/recovery/micro-scratchpads/{id:N}.json"] = bytes,
            [$"app/recovery/minipads/{id:N}.json"] = identical ? bytes : DraftBytes(id, "canonical")
        };
    }

    private static byte[] DraftBytes(Guid id, string content) =>
        JsonSerializer.SerializeToUtf8Bytes(new MiniPadRecoveryDraft(id, content, DateTime.UtcNow));

    private static FullBackupManifest WriteSnapshot(
        string path, string appData, string notes, Dictionary<string, byte[]> files)
    {
        var entries = new List<FullBackupEntry>();
        foreach (var (logicalPath, bytes) in files)
        {
            var filePath = BackupFormat.GetContainedPath(Path.Combine(path, "files"), logicalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, bytes);
            entries.Add(new FullBackupEntry(logicalPath, Path.Combine(appData, logicalPath[4..]),
                bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), null, null));
        }
        var manifest = new FullBackupManifest(1, Guid.NewGuid(), FullBackupType.User, DateTime.UtcNow, appData, notes, entries);
        File.WriteAllBytes(Path.Combine(path, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.JsonOptions));
        return manifest;
    }

    private static void WriteRequest(string backupDirectory, Guid backupId, Guid? importId) =>
        File.WriteAllBytes(Path.Combine(backupDirectory, "restore-request.json"),
            JsonSerializer.SerializeToUtf8Bytes(new FullBackupRestoreRequest(1, backupId, DateTime.UtcNow, importId), BackupFormat.JsonOptions));
}
