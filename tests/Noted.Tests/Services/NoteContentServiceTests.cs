using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteContentServiceTests
{
    [TestMethod]
    public void SaveVerifiesCurrentNoteAndKeepsPreviousTextInHistory()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var notePath = Path.Combine(notesDirectory, "note.txt");
        File.WriteAllText(notePath, "previous text");
        var service = new NoteContentService(directory.Path);

        var warning = service.Save(notePath, "current text");

        Assert.IsNull(warning);
        Assert.AreEqual("current text", File.ReadAllText(notePath));
        var historyPath = Directory.GetFiles(
            directory.File("note-history"),
            "*.json",
            SearchOption.AllDirectories).Single();
        using var history = JsonDocument.Parse(File.ReadAllText(historyPath));
        Assert.AreEqual("previous text", history.RootElement.GetProperty("Content").GetString());
        Assert.AreEqual(notePath, history.RootElement.GetProperty("NotePath").GetString());
    }

    [TestMethod]
    public void DuplicateSaveDoesNotCreateHistory()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var notePath = Path.Combine(notesDirectory, "note.txt");
        File.WriteAllText(notePath, "same text");
        var service = new NoteContentService(directory.Path);

        service.Save(notePath, "same text");

        Assert.IsFalse(Directory.Exists(directory.File("note-history")));
    }

    [TestMethod]
    public void SaveAsCreatesAndVerifiesSupportedTextFile()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var destination = Path.Combine(notesDirectory, "note.json");
        var service = new NoteContentService(directory.Path);

        var warning = service.SaveAs(destination, "{\"saved\":true}");

        Assert.IsNull(warning);
        Assert.AreEqual("{\"saved\":true}", File.ReadAllText(destination));
    }

    [TestMethod]
    public void LoadAllowsSupportedTextFileOutsideNotesFolder()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var externalPath = directory.File("external.log");
        File.WriteAllText(externalPath, "external text");
        var service = new NoteContentService(directory.Path);

        Assert.AreEqual("external text", service.Load(externalPath));
    }

    [TestMethod]
    public void LoadRejectsUnsupportedBinaryFile()
    {
        using var directory = new TemporaryTestDirectory();
        var binaryPath = directory.File("program.exe");
        File.WriteAllBytes(binaryPath, [0x4D, 0x5A]);
        var service = new NoteContentService(directory.Path);

        Assert.ThrowsExactly<ArgumentException>(() => service.Load(binaryPath));
    }

    [TestMethod]
    public void HistoryFailureWarnsButDoesNotBlockVerifiedNoteSave()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var notePath = Path.Combine(notesDirectory, "note.txt");
        File.WriteAllText(notePath, "before");
        File.WriteAllText(directory.File("note-history"), "blocks history folder");
        var service = new NoteContentService(directory.Path);

        var warning = service.Save(notePath, "after");

        Assert.AreEqual("after", File.ReadAllText(notePath));
        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "previous version");
    }

    [TestMethod]
    public void HistoryRetentionKeepsConfiguredNumberOfVersions()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var notePath = Path.Combine(notesDirectory, "note.txt");
        File.WriteAllText(notePath, "zero");
        var service = new NoteContentService(
            directory.Path,
            TimeProvider.System,
            historyLimit: 2);

        service.Save(notePath, "one");
        service.Save(notePath, "two");
        service.Save(notePath, "three");

        Assert.HasCount(
            2,
            Directory.GetFiles(
                directory.File("note-history"),
                "*.json",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    public void LockedCurrentNoteIsNotReplacedAndPreviousTextRemainsInHistory()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var notePath = Path.Combine(notesDirectory, "note.txt");
        File.WriteAllText(notePath, "before");
        var service = new NoteContentService(directory.Path);

        using (new FileStream(notePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsExactly<IOException>(() => service.Save(notePath, "after"));
            Assert.ThrowsExactly<IOException>(() => service.Save(notePath, "after"));
        }

        Assert.AreEqual("before", File.ReadAllText(notePath));
        Assert.HasCount(
            1,
            Directory.GetFiles(
                directory.File("note-history"),
                "*.json",
                SearchOption.AllDirectories));
    }
}
