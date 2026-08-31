using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEditorSessionServiceTests
{
    [TestMethod]
    public void DuplicateSessionDoesNotRotateRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new NoteEditorSessionService(directory.Path);
        var session = Session(directory.File("notes", "one.txt"), caretIndex: 4);

        service.Save(session);
        service.Save(session);

        Assert.IsTrue(File.Exists(directory.File("session", "editor-workspace.json")));
        Assert.IsFalse(File.Exists(directory.File("session", "editor-workspace.json.bak")));
    }

    [TestMethod]
    public void CorruptPrimaryRestoresBackupWithoutOverwritingItOnNextSave()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new NoteEditorSessionService(directory.Path);
        service.Save(Session(directory.File("notes", "one.txt"), caretIndex: 1));
        service.Save(Session(directory.File("notes", "one.txt"), caretIndex: 2));
        var primaryPath = directory.File("session", "editor-workspace.json");
        var backupPath = $"{primaryPath}.bak";
        var backupBeforeRecovery = File.ReadAllText(backupPath);
        File.WriteAllText(primaryPath, "{broken");

        var recoveredService = new NoteEditorSessionService(directory.Path);
        var loadResult = recoveredService.Load();
        recoveredService.Save(Session(directory.File("notes", "one.txt"), caretIndex: 3));

        Assert.AreEqual(1, loadResult.Session.Tabs.Single().CaretIndex);
        Assert.AreEqual(backupBeforeRecovery, File.ReadAllText(backupPath));
        Assert.HasCount(1, Directory.GetFiles(directory.File("session"), "*.corrupt-*"));
    }

    private static NoteEditorSession Session(string path, int caretIndex) =>
        new()
        {
            Tabs =
            [
                new NoteEditorTabState
                {
                    FilePath = path,
                    CaretIndex = caretIndex,
                    VerticalOffset = 12.5,
                    MarkdownPreviewEnabled = true
                }
            ],
            ActiveFilePath = path
        };
}
