using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupFormatTests
{
    [TestMethod]
    public void ManifestKeepsVersionOnePropertyNamesAndEnumValues()
    {
        var backupId = Guid.NewGuid();
        var manifest = new FullBackupManifest(
            BackupFormat.BackupSchemaVersion,
            backupId,
            FullBackupType.User,
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            @"C:\app",
            @"C:\notes",
            [new FullBackupEntry("notes/note.txt", @"C:\notes\note.txt", 4, "ABCD", null, 4)]);

        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.JsonOptions));
        var root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual(backupId, root.GetProperty("SnapshotId").GetGuid());
        Assert.AreEqual("Protected", root.GetProperty("Role").GetString());
        Assert.IsFalse(root.TryGetProperty("BackupId", out _));
        Assert.IsFalse(root.TryGetProperty("Type", out _));
    }

    [TestMethod]
    public void LogicalPathsUseStableForwardSlashNames()
    {
        Assert.AreEqual(
            "app/session/editor-workspace.json",
            BackupFormat.ToLogicalPath("app", @"session\editor-workspace.json"));
        Assert.AreEqual(
            "notes/folder/note.txt",
            BackupFormat.NormalizeLogicalPath(@"\notes\folder\note.txt"));
    }

    [TestMethod]
    public void ContainedPathRejectsTraversalOutsideBackupFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var root = directory.File("files");
        Directory.CreateDirectory(root);

        var contained = BackupFormat.GetContainedPath(root, "notes/note.txt");
        Assert.AreEqual(Path.Combine(root, "notes", "note.txt"), contained);
        Assert.ThrowsExactly<IOException>(() =>
            BackupFormat.GetContainedPath(root, "../outside.txt"));
    }

    [TestMethod]
    [DataRow("notes/note.md", "Notes", nameof(BackupContentFormat.Text))]
    [DataRow("app/checklist.json", "Checklist", nameof(BackupContentFormat.Json))]
    [DataRow("app/scratchpad.rtf", "Scratchpad", nameof(BackupContentFormat.RichText))]
    [DataRow("app/recovery/draft.json", "Open notes", nameof(BackupContentFormat.Json))]
    public void PreviewClassificationRemainsPartOfTheFormat(
        string logicalPath,
        string expectedCategory,
        string expectedFormatName)
    {
        Assert.AreEqual(expectedCategory, BackupFormat.GetFileCategory(logicalPath));
        Assert.AreEqual(
            Enum.Parse<BackupContentFormat>(expectedFormatName),
            BackupFormat.GetContentFormat(logicalPath));
    }
}
