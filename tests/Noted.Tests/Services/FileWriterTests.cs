using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileWriterTests
{
    [TestMethod]
    public void InitialWriteCreatesFileWithoutLeavingTemporaryFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");

        var result = FileWriter.WriteAllText(path, "first");

        Assert.AreEqual("first", File.ReadAllText(path));
        Assert.IsTrue(result.FileUpdated);
        Assert.IsFalse(result.BackupUpdated);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void ByteWriteReplacesTheFileWithoutChangingItsContents()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        var result = FileWriter.WriteAllBytes(path, [0, 255, 4, 8]);

        CollectionAssert.AreEqual(new byte[] { 0, 255, 4, 8 }, File.ReadAllBytes(path));
        Assert.IsTrue(result.FileUpdated);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void ReplacementMovesPreviousFileIntoRollingBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");
        var backupPath = directory.File("content.txt.bak");
        FileWriter.WriteAllText(path, "first", backupPath);
        FileWriter.WriteAllText(path, "second", backupPath);

        var result = FileWriter.WriteAllText(path, "third", backupPath);

        Assert.AreEqual("third", File.ReadAllText(path));
        Assert.AreEqual("second", File.ReadAllText(backupPath));
        Assert.IsTrue(result.BackupUpdated);
        Assert.IsNull(result.Warning);
    }

    [TestMethod]
    public void ReplacementCreatesRollingBackupWhenNoneExists()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");
        var backupPath = directory.File("content.txt.bak");
        File.WriteAllText(path, "first");

        var result = FileWriter.WriteAllText(path, "second", backupPath);

        Assert.AreEqual("second", File.ReadAllText(path));
        Assert.AreEqual("first", File.ReadAllText(backupPath));
        Assert.IsTrue(result.FileUpdated);
        Assert.IsTrue(result.BackupUpdated);
        Assert.IsNull(result.Warning);
    }

    [TestMethod]
    public void BackupOutsideDestinationDirectoryIsRejectedWithoutChangingFile()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");
        var otherDirectory = directory.File("other");
        Directory.CreateDirectory(otherDirectory);
        var backupPath = Path.Combine(otherDirectory, "content.txt.bak");
        File.WriteAllText(path, "existing");

        Assert.ThrowsExactly<ArgumentException>(
            () => FileWriter.WriteAllText(path, "replacement", backupPath));

        Assert.AreEqual("existing", File.ReadAllText(path));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void LockedBackupKeepsSavedFileAndPreservesRollbackCopy()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");
        var backupPath = directory.File("content.txt.bak");
        FileWriter.WriteAllText(path, "first", backupPath);
        FileWriter.WriteAllText(path, "second", backupPath);

        FileWriteResult result;
        using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = FileWriter.WriteAllText(path, "third", backupPath);
        }

        Assert.AreEqual("third", File.ReadAllText(path));
        Assert.IsNotNull(result.Warning);
        Assert.IsNotNull(result.PreservedBackupPath);
        Assert.AreEqual("second", File.ReadAllText(result.PreservedBackupPath));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void FailedReplacementLeavesExistingFileUnchangedAndCleansTemporaryFile()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");
        File.WriteAllText(path, "existing");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsExactly<IOException>(() => FileWriter.WriteAllText(path, "replacement"));
        }

        Assert.AreEqual("existing", File.ReadAllText(path));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void InitialMoveFailureCleansTemporaryFile()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("destination-conflict.txt");
        Directory.CreateDirectory(path);

        Assert.ThrowsExactly<IOException>(() => FileWriter.WriteAllText(path, "content"));

        Assert.IsTrue(Directory.Exists(path));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
