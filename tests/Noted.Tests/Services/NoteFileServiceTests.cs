using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteFileServiceTests
{
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

        var fileName = service.CreateNote("new note");

        var path = Path.Combine(notesDirectory, fileName);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(string.Empty, File.ReadAllText(path));
        Assert.HasCount(0, Directory.GetFiles(notesDirectory, "*.tmp"));
    }
}
