using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteContentServiceTests
{
    [TestMethod]
    public void FirstSaveAsToTheSamePathNeverRemovesTheSavedFile()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("new note.txt");
        File.WriteAllText(path, "initial");
        var service = new NoteContentService(directory.Path);

        Assert.IsNull(service.SaveNewNoteAs(path, path, "edited", "initial"));
        Assert.AreEqual("edited", File.ReadAllText(path));
    }

    [TestMethod]
    public void FirstSaveAsRemovesOnlyTheUnchangedInitialFile()
    {
        using var directory = new TemporaryTestDirectory();
        var source = directory.File("new note.txt");
        var destination = directory.File("chosen name.txt");
        const string initial = "2026-09-08 12:00:00";
        File.WriteAllText(source, initial);
        var service = new NoteContentService(directory.Path);

        Assert.IsNull(service.SaveNewNoteAs(source, destination, "written note", initial));

        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("written note", File.ReadAllText(destination));
    }

    [TestMethod]
    public void FirstSaveAsKeepsAnExternallyChangedInitialFile()
    {
        using var directory = new TemporaryTestDirectory();
        var source = directory.File("new note.txt");
        var destination = directory.File("chosen name.txt");
        File.WriteAllText(source, "external edits");
        var service = new NoteContentService(directory.Path);

        var warning = service.SaveNewNoteAs(source, destination, "editor text", "");

        Assert.IsNotNull(warning);
        Assert.AreEqual("external edits", File.ReadAllText(source));
        Assert.AreEqual("editor text", File.ReadAllText(destination));
    }

    [TestMethod]
    public void FailedFirstSaveAsKeepsTheInitialFile()
    {
        using var directory = new TemporaryTestDirectory();
        var source = directory.File("new note.txt");
        var destination = directory.File("blocked.txt");
        File.WriteAllText(source, "initial");
        Directory.CreateDirectory(destination);
        var service = new NoteContentService(directory.Path);

        Assert.Throws<Exception>(() => service.SaveNewNoteAs(source, destination, "edited", "initial"));
        Assert.AreEqual("initial", File.ReadAllText(source));
    }

    [TestMethod]
    public void SaveAsForAnEstablishedNoteStillKeepsTheOriginal()
    {
        using var directory = new TemporaryTestDirectory();
        var source = directory.File("original.txt");
        var destination = directory.File("copy.txt");
        File.WriteAllText(source, "original text");
        var service = new NoteContentService(directory.Path);

        service.SaveAs(destination, "edited copy");

        Assert.AreEqual("original text", File.ReadAllText(source));
        Assert.AreEqual("edited copy", File.ReadAllText(destination));
    }

    [TestMethod]
    public void FirstSaveAsWarnsIfTheInitialFileIsInUse()
    {
        using var directory = new TemporaryTestDirectory();
        var source = directory.File("new note.txt");
        var destination = directory.File("chosen name.txt");
        File.WriteAllText(source, "");
        using var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var service = new NoteContentService(directory.Path);

        Assert.IsNotNull(service.SaveNewNoteAs(source, destination, "edited", ""));
        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual("edited", File.ReadAllText(destination));
    }

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
