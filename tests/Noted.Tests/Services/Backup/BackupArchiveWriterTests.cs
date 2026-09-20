using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupArchiveWriterTests
{
    [TestMethod]
    public void BuildVerifiedBackupCreatesAReadableArchive()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        var backupDirectory = Path.Combine(appData, "full-snapshots");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        var reader = CreateReader(appData, notes);
        var writer = new BackupArchiveWriter(
            appData,
            notes,
            backupDirectory,
            TimeProvider.System,
            reader);
        var source = WriteSource(notes, "one.txt", "note one");

        var build = writer.BuildVerifiedBackup(FullBackupType.User, [source]);
        var result = reader.ReadBackup(build.Path, FullBackupType.User);

        Assert.AreEqual(BackupReadStatus.Valid, result.Status);
        Assert.AreEqual("notes/one.txt", result.Manifest!.Entries.Single().LogicalPath);
        Assert.AreEqual("note one", File.ReadAllText(Path.Combine(build.Path, "files", "notes", "one.txt")));
    }

    [TestMethod]
    public void ReplacingAUserBackupPreservesThePreviousVerifiedArchive()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        var backupDirectory = Path.Combine(appData, "full-snapshots");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        var reader = CreateReader(appData, notes);
        var writer = new BackupArchiveWriter(
            appData,
            notes,
            backupDirectory,
            TimeProvider.System,
            reader);
        var firstSource = WriteSource(notes, "one.txt", "first version");
        var firstBuild = writer.BuildVerifiedBackup(FullBackupType.User, [firstSource]);
        writer.ReplaceBackupFolder(firstBuild.Path, FullBackupType.User);
        var secondSource = WriteSource(notes, "one.txt", "second version");
        var secondBuild = writer.BuildVerifiedBackup(FullBackupType.User, [secondSource]);

        var replacement = writer.ReplaceBackupFolder(secondBuild.Path, FullBackupType.User);

        Assert.IsNull(replacement.Warning);
        var protectedPath = Path.Combine(backupDirectory, "protected");
        var previousPath = Path.Combine(backupDirectory, "previous");
        Assert.AreEqual(BackupReadStatus.Valid, reader.ReadBackup(protectedPath, FullBackupType.User).Status);
        Assert.AreEqual(BackupReadStatus.Valid, reader.ReadBackup(previousPath, FullBackupType.User).Status);
        Assert.AreEqual(
            "second version",
            File.ReadAllText(Path.Combine(protectedPath, "files", "notes", "one.txt")));
        Assert.AreEqual(
            "first version",
            File.ReadAllText(Path.Combine(previousPath, "files", "notes", "one.txt")));
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

    private static BackupSourceEntry WriteSource(
        string notesDirectory,
        string fileName,
        string content)
    {
        var path = Path.Combine(notesDirectory, fileName);
        File.WriteAllText(path, content);
        var data = Encoding.UTF8.GetBytes(content);
        return new BackupSourceEntry(
            $"notes/{fileName}",
            path,
            data.LongLength,
            Convert.ToHexString(SHA256.HashData(data)),
            null,
            data.LongLength);
    }
}
