using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupArchiveReaderTests
{
    [TestMethod]
    public void ReadBackupVerifiesFilesAndBuildsPreview()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        var backup = directory.File("backup");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        var notePath = Path.Combine(notes, "one.txt");
        File.WriteAllText(notePath, "note one");
        var backupId = WriteBackup(backup, appData, notes, "note one");
        var reader = CreateReader(appData, notes);

        var result = reader.ReadBackup(backup, FullBackupType.User);
        var preview = reader.GetBackupPreview(backup, FullBackupType.User);
        var content = reader.ReadBackupFile(backup, FullBackupType.User, backupId, preview.Files.Single());

        Assert.AreEqual(BackupReadStatus.Valid, result.Status);
        Assert.IsTrue(preview.IsValid);
        Assert.AreEqual(BackupFileComparison.Unchanged, preview.Files.Single().Comparison);
        Assert.IsTrue(content.Success);
        Assert.AreEqual("note one", Encoding.UTF8.GetString(content.Data!));
    }

    [TestMethod]
    public void ReadBackupRejectsContentThatDoesNotMatchTheManifest()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        var backup = directory.File("backup");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        WriteBackup(backup, appData, notes, "original");
        File.WriteAllText(Path.Combine(backup, "files", "notes", "one.txt"), "changed");

        var result = CreateReader(appData, notes).ReadBackup(backup, FullBackupType.User);

        Assert.AreEqual(BackupReadStatus.Invalid, result.Status);
        StringAssert.Contains(result.Error, "failed hash verification");
    }

    [TestMethod]
    public void ImportPreviewRejectsUnsafeArchivePaths()
    {
        using var directory = new TemporaryTestDirectory();
        var archivePath = directory.File("unsafe.notedbackup");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        var preview = CreateReader(directory.File("app-data"), directory.File("notes"))
            .GetImportPreview(archivePath);

        Assert.IsTrue(preview.Exists);
        Assert.IsFalse(preview.IsValid);
        Assert.AreEqual("The selected backup could not be read or verified.", preview.Error);
    }

    private static BackupArchiveReader CreateReader(string appData, string notes) =>
        new(
            (logicalPath, manifest) => logicalPath.StartsWith("notes/", StringComparison.OrdinalIgnoreCase)
                ? BackupFormat.GetContainedPath(manifest.NotesRoot, logicalPath["notes/".Length..])
                : BackupFormat.GetContainedPath(manifest.AppDataRoot, logicalPath["app/".Length..]),
            (entry, bytes, _) => new ImportedBackupFile(
                entry.LogicalPath,
                entry.LogicalPath,
                bytes,
                entry.ItemCount,
                entry.ContentLength),
            (manifest, backupId, files) => manifest with
            {
                BackupId = backupId,
                AppDataRoot = appData,
                NotesRoot = notes,
                Entries = files.Select(file => new FullBackupEntry(
                    file.LogicalPath,
                    file.LogicalPath,
                    file.Length,
                    file.Sha256,
                    file.ItemCount,
                    file.ContentLength)).ToList()
            });

    private static Guid WriteBackup(
        string backupPath,
        string appData,
        string notes,
        string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        var filePath = Path.Combine(backupPath, "files", "notes", "one.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, data);
        var backupId = Guid.NewGuid();
        var manifest = new FullBackupManifest(
            BackupFormat.BackupSchemaVersion,
            backupId,
            FullBackupType.User,
            DateTime.UtcNow,
            appData,
            notes,
            [
                new FullBackupEntry(
                    "notes/one.txt",
                    Path.Combine(notes, "one.txt"),
                    data.LongLength,
                    Convert.ToHexString(SHA256.HashData(data)),
                    null,
                    data.LongLength)
            ]);
        File.WriteAllText(
            Path.Combine(backupPath, BackupFormat.ManifestFileName),
            JsonSerializer.Serialize(manifest, BackupFormat.JsonOptions));
        return backupId;
    }
}
