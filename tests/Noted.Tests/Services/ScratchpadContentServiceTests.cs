using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ScratchpadContentServiceTests
{
    [TestMethod]
    public void DuplicateContentDoesNotRotateRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ScratchpadContentService(directory.Path);

        service.TrySaveContent("same content");
        service.TrySaveContent("same content");

        Assert.IsTrue(File.Exists(directory.File("scratchpad.rtf")));
        Assert.IsFalse(File.Exists(directory.File("scratchpad.rtf.bak")));
    }

    [TestMethod]
    public void MissingPrimaryRestoresRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ScratchpadContentService(directory.Path);
        service.TrySaveContent("previous content");
        service.TrySaveContent("current content");
        File.Delete(directory.File("scratchpad.rtf"));

        var loadResult = new ScratchpadContentService(directory.Path).TryLoadContent();

        Assert.IsTrue(loadResult.Success);
        Assert.AreEqual("previous content", loadResult.Content);
        Assert.IsNotNull(loadResult.Warning);
    }

    [TestMethod]
    public void LockedBackupWarnsWithoutTurningVerifiedSaveIntoFailure()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ScratchpadContentService(directory.Path);
        service.TrySaveContent("one");
        service.TrySaveContent("two");
        var backupPath = directory.File("scratchpad.rtf.bak");

        ScratchpadContentSaveResult result;
        using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = service.TrySaveContent("three");
        }

        Assert.IsTrue(result.Success);
        Assert.IsNull(result.Error);
        Assert.IsNotNull(result.Warning);
        Assert.AreEqual(
            "three",
            new ScratchpadContentService(directory.Path).TryLoadContent().Content);
        Assert.HasCount(1, Directory.GetFiles(directory.Path, "scratchpad.rtf.bak.pending-*"));
    }

}
