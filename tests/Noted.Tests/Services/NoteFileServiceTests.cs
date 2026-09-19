using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteFileServiceTests
{
    [TestMethod]
    [DataRow("external")]
    [DataRow("sibling-prefix")]
    [DataRow("parent-traversal")]
    public void ExternalRenamePathsHaveNoNoteKeyAndLeaveFilesUnchanged(string pathKind)
    {
        using var directory = new TemporaryTestDirectory();
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        var externalDirectory = directory.File(pathKind == "sibling-prefix" ? "notes-other" : "external");
        Directory.CreateDirectory(externalDirectory);
        var externalPath = Path.Combine(externalDirectory, "outside.txt");
        File.WriteAllText(externalPath, "Keep this content");
        var requestedPath = pathKind == "parent-traversal"
            ? Path.Combine(notes, "..", "external", "outside.txt")
            : externalPath;
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(notes);
        using var service = new NoteFileService(settings);

        Assert.IsFalse(service.TryGetNoteKey(requestedPath, out var noteKey));
        Assert.AreEqual(string.Empty, noteKey);
        var (success, renamedFileName, error) = service.RenameNote(
            Path.GetFileName(requestedPath), "Renamed", Path.GetDirectoryName(requestedPath));
        Assert.IsFalse(success);
        Assert.IsNull(renamedFileName);
        Assert.IsNotNull(error);
        Assert.AreEqual("Keep this content", File.ReadAllText(externalPath));
        Assert.IsFalse(File.Exists(Path.Combine(externalDirectory, "Renamed.txt")));
        Assert.HasCount(0, Directory.GetFiles(notes));
    }

    [TestMethod]
    public void NormalizedInternalRenamePathsKeepTheirExistingNoteKeys()
    {
        using var directory = new TemporaryTestDirectory();
        var notes = directory.File("notes");
        Directory.CreateDirectory(Path.Combine(notes, "child"));
        var notePath = Path.Combine(notes, "original.txt");
        File.WriteAllText(notePath, "Keep this content");
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(notes);
        using var service = new NoteFileService(settings);
        var requestedPath = Path.Combine(notes, "child", "..", "original.txt");

        Assert.IsTrue(service.TryGetNoteKey(requestedPath, out var noteKey));
        Assert.AreEqual(service.GetNoteKey(notePath), noteKey);
        var (success, renamedFileName, error) = service.RenameNote("original.txt", "Renamed", notes);
        Assert.IsTrue(success);
        Assert.IsNull(error);
        Assert.IsNotNull(renamedFileName);
        Assert.IsFalse(File.Exists(notePath));
        var renamedPath = Path.Combine(notes, renamedFileName);
        Assert.AreEqual("Keep this content", File.ReadAllText(renamedPath));
        Assert.IsTrue(service.TryGetNoteKey(renamedPath, out var renamedKey));
        Assert.AreEqual(service.GetNoteKey(renamedPath), renamedKey);
    }

    [TestMethod]
    public void AcceptingSuggestedDateNameCountsAsAnExplicitName()
    {
        using var directory = new TemporaryTestDirectory();
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        var settings = new AppSettingsService(directory.File("app-data"));
        settings.SaveNotesDirectory(notes);
        using var service = new NoteFileService(settings);
        var suggestion = service.SuggestNoteName();
        var created = service.CreateNote(suggestion);
        Assert.IsFalse(created.UsesGeneratedName);
        Assert.AreEqual(suggestion + ".txt", created.FileName);
        Assert.AreNotEqual(suggestion, service.SuggestNoteName());
        Assert.IsTrue(service.RenameNote(created.FileName, suggestion).Success);
    }

    [TestMethod]
    public void CreateNoteWritesCompleteInitialContent()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var settings = new AppSettingsService(directory.File("app-data"));
        settings.SaveNotesDirectory(notesDirectory);
        settings.SaveTimestampPlacement(NoteTimestampPlacement.None);
        using var service = new NoteFileService(settings);

        var createdNote = service.CreateNote("new note");

        var path = Path.Combine(notesDirectory, createdNote.FileName);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(string.Empty, File.ReadAllText(path));
        Assert.IsFalse(createdNote.UsesGeneratedName);
        Assert.HasCount(0, Directory.GetFiles(notesDirectory, "*.tmp"));
    }

    [TestMethod]
    public void CreateNoteCanPlaceTimestampOnChosenLine()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var settings = new AppSettingsService(directory.File("app-data"));
        settings.SaveNotesDirectory(notesDirectory);
        settings.SaveTimestampPlacement(NoteTimestampPlacement.Line);
        settings.SaveTimestampLine(4);
        using var service = new NoteFileService(settings);

        var createdNote = service.CreateNote("line timestamp");
        var lines = File.ReadAllLines(Path.Combine(notesDirectory, createdNote.FileName));

        Assert.HasCount(4, lines);
        Assert.IsTrue(DateTime.TryParse(lines[3], out _));
    }

    [TestMethod]
    public void QuickNoteReportsThatItUsesAGeneratedName()
    {
        using var directory = new TemporaryTestDirectory();
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var settings = new AppSettingsService(directory.File("app-data"));
        settings.SaveNotesDirectory(notesDirectory);
        using var service = new NoteFileService(settings);

        var createdNote = service.CreateNote();

        Assert.IsTrue(createdNote.UsesGeneratedName);
        Assert.IsTrue(File.Exists(Path.Combine(notesDirectory, createdNote.FileName)));
    }
}
