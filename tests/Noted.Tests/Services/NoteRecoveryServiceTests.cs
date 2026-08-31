using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteRecoveryServiceTests
{
    [TestMethod]
    public void DuplicateContentDoesNotRotateRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var notePath = directory.File("notes", "note.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
        var service = new NoteRecoveryService(directory.Path);

        service.SaveDraft(notePath, "same content");
        service.SaveDraft(notePath, "same content");

        Assert.HasCount(1, Directory.GetFiles(directory.File("recovery"), "*.json"));
        Assert.HasCount(0, Directory.GetFiles(directory.File("recovery"), "*.bak"));
    }

    [TestMethod]
    public void RollingBackupRestoresPreviousDraft()
    {
        using var directory = new TemporaryTestDirectory();
        var notePath = directory.File("notes", "note.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
        var service = new NoteRecoveryService(directory.Path);
        service.SaveDraft(notePath, "previous draft");
        service.SaveDraft(notePath, "current draft");
        var primaryPath = Directory.GetFiles(directory.File("recovery"), "*.json").Single();
        File.WriteAllText(primaryPath, "{broken");

        var loadResult = new NoteRecoveryService(directory.Path).LoadDraft(notePath);

        Assert.AreEqual("previous draft", loadResult.Draft?.Content);
        Assert.IsTrue(loadResult.Issues.Count > 0);
    }
}
