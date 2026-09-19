using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupContentCatalogTests
{
    [TestMethod]
    public void CollectDiscoversSupportedContentWithStableMetadataAndOrdering()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        var backup = Path.Combine(appData, "full-snapshots");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        Directory.CreateDirectory(Path.Combine(appData, "DeletedNotes"));
        File.WriteAllText(Path.Combine(appData, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(appData, "checklist.json"), "[{},{}]");
        File.WriteAllText(Path.Combine(appData, "scratchpad.rtf"), "{\\rtf1 note}");
        File.WriteAllText(Path.Combine(appData, "DeletedNotes", "old.txt"), "old");
        var notePath = Path.Combine(notes, "note.md");
        File.WriteAllText(notePath, "note content");
        File.WriteAllText(Path.Combine(notes, "ignored.exe"), "ignored");
        var catalog = new BackupContentCatalog(appData, notes, backup);

        var entries = catalog.Collect();

        CollectionAssert.AreEqual(
            new[]
            {
                "app/checklist.json",
                "app/DeletedNotes/old.txt",
                "app/scratchpad.rtf",
                "app/settings.json",
                "notes/note.md"
            },
            entries.Select(entry => entry.LogicalPath).ToArray());
        Assert.AreEqual(2, entries.Single(entry => entry.LogicalPath == "app/checklist.json").ItemCount);
        Assert.AreEqual(
            "note content".Length,
            entries.Single(entry => entry.LogicalPath == "notes/note.md").ContentLength);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("note content"))),
            entries.Single(entry => entry.LogicalPath == "notes/note.md").Sha256);
    }

    [TestMethod]
    public void UpdatingNotesDirectoryChangesTheCatalogSource()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var firstNotes = directory.File("first-notes");
        var secondNotes = directory.File("second-notes");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(firstNotes);
        Directory.CreateDirectory(secondNotes);
        File.WriteAllText(Path.Combine(firstNotes, "first.txt"), "first");
        File.WriteAllText(Path.Combine(secondNotes, "second.txt"), "second");
        var catalog = new BackupContentCatalog(
            appData,
            firstNotes,
            Path.Combine(appData, "full-snapshots"));

        Assert.AreEqual("notes/first.txt", catalog.Collect().Single().LogicalPath);

        catalog.UpdateNotesDirectory(secondNotes);

        Assert.AreEqual("notes/second.txt", catalog.Collect().Single().LogicalPath);
    }

    [TestMethod]
    public void CollectRejectsInvalidCatalogContentBeforeBackupWritingBegins()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(appData, "checklist.json"), "[null]");
        var catalog = new BackupContentCatalog(
            appData,
            notes,
            Path.Combine(appData, "full-snapshots"));

        Assert.ThrowsExactly<JsonException>(() => catalog.Collect());
    }

    [TestMethod]
    public void CollectRequiresTheConfiguredNotesDirectory()
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app-data");
        Directory.CreateDirectory(appData);
        var missingNotes = directory.File("missing-notes");
        var catalog = new BackupContentCatalog(
            appData,
            missingNotes,
            Path.Combine(appData, "full-snapshots"));

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => catalog.Collect());
    }
}
