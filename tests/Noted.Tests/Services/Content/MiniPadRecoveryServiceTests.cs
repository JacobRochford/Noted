using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class MiniPadRecoveryServiceTests
{
    [TestMethod]
    public void RecoveryAndWriteBlockingAreIndependentForEachInstance()
    {
        using var directory = new TemporaryTestDirectory();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = new MiniPadRecoveryService(directory.Path, firstId);
        var second = new MiniPadRecoveryService(directory.Path, secondId);
        first.SaveDraft("first");
        first.SaveDraft("first edited");
        second.SaveDraft("second");
        var firstPath = directory.File("recovery", "minipads", $"{firstId:N}.json");
        using var primaryLock = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var backupLock = new FileStream($"{firstPath}.bak", FileMode.Open, FileAccess.Read, FileShare.None);
        var blocked = new MiniPadRecoveryService(directory.Path, firstId);

        Assert.IsNull(blocked.LoadDraft().Draft);
        Assert.ThrowsExactly<IOException>(() => blocked.SaveDraft("blocked"));
        second.SaveDraft("second edited");

        var restored = new MiniPadRecoveryService(directory.Path, secondId).LoadDraft();
        Assert.AreEqual(secondId, restored.Draft!.Id);
        Assert.AreEqual("second edited", restored.Draft.Content);
        Assert.HasCount(0, restored.Issues);
    }

    [TestMethod]
    public void DuplicateContentDoesNotRotateRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new MiniPadRecoveryService(directory.Path);

        service.SaveDraft("same content");
        service.SaveDraft("same content");

        var recoveryDirectory = directory.File("recovery", "minipads");
        Assert.HasCount(1, Directory.GetFiles(recoveryDirectory, "*.json"));
        Assert.HasCount(0, Directory.GetFiles(recoveryDirectory, "*.bak"));
    }

    [TestMethod]
    public void CorruptPrimaryRestoresBackupWithoutOverwritingItOnNextSave()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new MiniPadRecoveryService(directory.Path);
        service.SaveDraft("backup content");
        service.SaveDraft("newer content");
        var recoveryDirectory = directory.File("recovery", "minipads");
        var primaryPath = Directory.GetFiles(recoveryDirectory, "*.json").Single();
        var backupPath = $"{primaryPath}.bak";
        var backupBeforeRecovery = File.ReadAllText(backupPath);
        File.WriteAllText(primaryPath, "{broken");

        var recoveredService = new MiniPadRecoveryService(directory.Path);
        var loadResult = recoveredService.LoadDraft();
        var warning = recoveredService.SaveDraft("edited after recovery");

        Assert.AreEqual("backup content", loadResult.Draft?.Content);
        Assert.IsNull(warning);
        Assert.AreEqual(backupBeforeRecovery, File.ReadAllText(backupPath));
        Assert.HasCount(1, Directory.GetFiles(recoveryDirectory, "*.corrupt-*"));
    }

    [TestMethod]
    public void UnreadablePrimaryAndBackupBlockFurtherWrites()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new MiniPadRecoveryService(directory.Path);
        service.SaveDraft("one");
        service.SaveDraft("two");
        var recoveryDirectory = directory.File("recovery", "minipads");
        var primaryPath = Directory.GetFiles(recoveryDirectory, "*.json").Single();
        var backupPath = $"{primaryPath}.bak";

        using var primaryLock = new FileStream(
            primaryPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var backupLock = new FileStream(
            backupPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var blockedService = new MiniPadRecoveryService(directory.Path);

        Assert.IsNull(blockedService.LoadDraft().Draft);
        Assert.ThrowsExactly<IOException>(() => blockedService.SaveDraft("three"));
    }
}
