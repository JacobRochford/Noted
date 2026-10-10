using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileSystemLinkArchiveTests
{
    [TestMethod]
    [DataRow("archive")]
    [DataRow("source")]
    [DataRow("destination")]
    public void ArchiveRestoreRejectsLinksBeforeMovingOrCreatingFolders(string location)
    {
        using var directory = new TemporaryTestDirectory();
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        var settings = new AppSettingsService(directory.File("app"));
        settings.SaveNotesDirectory(notes);
        using var service = new NoteFileService(settings);
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "one.txt"), "sentinel");
        var linkPath = location switch
        {
            "archive" => Path.Combine(notes, ".archive"),
            "source" => Path.Combine(notes, ".archive", "nested"),
            _ => Path.Combine(notes, "nested")
        };
        var relative = location == "archive" ? "one.txt" : Path.Combine("nested", "one.txt");
        var archiveSource = Path.Combine(notes, ".archive", relative);
        if (location == "destination")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archiveSource)!);
            File.WriteAllText(archiveSource, "archived");
        }
        using var link = new TestFileSystemLink(linkPath, outside);

        Assert.IsFalse(service.RestoreArchivedNote(relative).Success);

        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "one.txt")));
        Assert.IsTrue(File.Exists(archiveSource));
        if (location != "destination")
            Assert.IsFalse(Directory.Exists(Path.Combine(notes, "nested")));
    }

    [TestMethod]
    public void NestedArchiveRestoreWorksThroughConfiguredLinkedNotesRoot()
    {
        using var directory = new TemporaryTestDirectory();
        var outside = directory.File("outside");
        Directory.CreateDirectory(Path.Combine(outside, ".archive", "nested"));
        File.WriteAllText(Path.Combine(outside, ".archive", "nested", "one.txt"), "archived");
        using var link = new TestFileSystemLink(directory.File("notes"), outside);
        var settings = new AppSettingsService(directory.File("app"));
        settings.SaveNotesDirectory(directory.File("notes"));
        using var service = new NoteFileService(settings);

        var result = service.RestoreArchivedNote(Path.Combine("nested", "one.txt"));

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("archived", File.ReadAllText(Path.Combine(outside, "nested", "one.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(outside, ".archive", "nested", "one.txt")));
    }
}
