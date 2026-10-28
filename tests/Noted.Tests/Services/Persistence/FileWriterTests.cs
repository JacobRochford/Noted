using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileWriterTests
{
    [TestMethod]
    public void PreservedReplacementCapturesTheFileChangedAfterStaging()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        var preserved = directory.File("conflict.txt");
        File.WriteAllText(path, "original");

        var result = FileWriter.WriteFile(path, null, temporary =>
        {
            File.WriteAllText(temporary, "editor edits");
            File.WriteAllText(path, "external");
        }, preserveReplacedFileAt: preserved);

        Assert.AreEqual("editor edits", File.ReadAllText(path));
        Assert.AreEqual("external", File.ReadAllText(preserved));
        Assert.AreEqual(preserved, result.PreservedBackupPath);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.pending-*"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreservedReplacementRejectsDestinationCollisions(bool duringStaging)
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        var preserved = directory.File("conflict.txt");
        File.WriteAllText(path, "original");
        if (!duringStaging) File.WriteAllText(preserved, "keep me");

        Assert.ThrowsExactly<IOException>(() => FileWriter.WriteFile(path, null, temporary =>
        {
            File.WriteAllText(temporary, "editor edits");
            File.WriteAllText(preserved, "keep me");
        }, preserveReplacedFileAt: preserved));

        Assert.AreEqual("original", File.ReadAllText(path));
        Assert.AreEqual("keep me", File.ReadAllText(preserved));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void PreservedReplacementRequiresAnExistingTarget()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("missing.txt");
        var preserved = directory.File("conflict.txt");

        Assert.Throws<IOException>(() => FileWriter.WriteAllBytes(path, [1, 2], preserveReplacedFileAt: preserved));

        Assert.HasCount(0, Directory.GetFiles(directory.Path));
    }

    [TestMethod]
    public void PreservedReplacementFailureKeepsOriginalAndCleansStaging()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("locked.txt");
        var preserved = directory.File("conflict.txt");
        File.WriteAllText(path, "original");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => FileWriter.WriteAllBytes(path, [1, 2], preserveReplacedFileAt: preserved));

        Assert.AreEqual("original", File.ReadAllText(path));
        Assert.IsFalse(File.Exists(preserved));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [TestMethod]
    public void PreservedReplacementRejectsOtherDirectoriesAndRollingBackups()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.txt");
        File.WriteAllText(path, "original");

        Assert.ThrowsExactly<ArgumentException>(() => FileWriter.WriteAllBytes(path, [1],
            preserveReplacedFileAt: directory.File("other", "conflict.txt")));
        Assert.ThrowsExactly<ArgumentException>(() => FileWriter.WriteAllBytes(path, [1],
            backupPath: path + ".bak", preserveReplacedFileAt: directory.File("conflict.txt")));

        Assert.AreEqual("original", File.ReadAllText(path));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
    }

    [TestMethod]
    public void InitialWriteCreatesFileWithoutLeavingTemporaryFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("content.txt");

        var result = FileWriter.WriteAllText(path, "first");

        Assert.AreEqual("first", File.ReadAllText(path));
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
