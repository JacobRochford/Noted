using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class MiniPadRecoveryMigrationTests
{
    [TestMethod]
    public void FreshInstallUsesStableStorageIdentifierWithoutADisplayName()
    {
        using var directory = new TemporaryTestDirectory();

        MiniPadRecoveryMigration.Migrate(directory.Path);
        Assert.IsFalse(Directory.Exists(directory.File("recovery")));
        var service = new MiniPadRecoveryService(directory.Path);
        service.SaveDraft("content");

        Assert.IsTrue(File.Exists(directory.File("recovery", "minipads", $"{service.Id:N}.json")));
        Assert.IsFalse(Directory.Exists(LegacyDirectory(directory)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MigrationPreservesAllDraftsEvidenceAndTimestamps(bool merge)
    {
        using var directory = new TemporaryTestDirectory();
        var source = LegacyDirectory(directory);
        var destination = MiniPadStorage.GetRecoveryDirectory(directory.Path);
        var ids = new[] { MiniPadRecoveryService.LegacyDraftId, Guid.NewGuid(), Guid.NewGuid() };
        var settings = new AppSettingsService(directory.Path);
        settings.SaveMiniPadWindowState(ids[0], new MiniPadWindowState { ReopenOnStartup = true });
        settings.SaveMiniPadWindowState(ids[1], new MiniPadWindowState { IsClosed = true });
        // the third draft has no saved window settings and must still migrate.
        var settingsBytes = File.ReadAllBytes(directory.File("settings.json"));
        foreach (var id in ids)
            Write(source, $"{id:N}.json", JsonSerializer.Serialize(new MiniPadRecoveryDraft(id, id.ToString(), DateTime.UtcNow)));
        foreach (var name in new[] { "draft.json.bak", "draft.json.corrupt-123", "draft.pending", ".draft.tmp", "nested/deeper/unexpected.bin" })
            Write(source, name, "evidence " + name);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var savedUtc = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), File.ReadAllBytes);
        foreach (var name in before.Keys)
            File.SetLastWriteTimeUtc(Path.Combine(source, name), savedUtc);
        if (merge)
        {
            Write(destination, "existing.json", "existing content");
            Write(destination, "nested/existing.tmp", "existing nested content");
        }

        MiniPadRecoveryMigration.Migrate(directory.Path);
        MiniPadRecoveryMigration.Migrate(directory.Path);

        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(Path.Combine(destination, "empty")));
        foreach (var (name, bytes) in before)
        {
            var path = Path.Combine(destination, name);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Assert.AreEqual(savedUtc, File.GetLastWriteTimeUtc(path));
        }
        foreach (var id in ids)
            Assert.AreEqual(id.ToString(), new MiniPadRecoveryService(directory.Path, id).LoadDraft().Draft!.Content);
        CollectionAssert.AreEqual(settingsBytes, File.ReadAllBytes(directory.File("settings.json")));
        if (merge)
        {
            Assert.AreEqual("existing content", File.ReadAllText(Path.Combine(destination, "existing.json")));
            Assert.AreEqual("existing nested content", File.ReadAllText(Path.Combine(destination, "nested/existing.tmp")));
        }
    }

    [TestMethod]
    public void RetryCompletesAnInterruptedMergeAndMigratesReintroducedData()
    {
        using var directory = new TemporaryTestDirectory();
        var source = LegacyDirectory(directory);
        var destination = MiniPadStorage.GetRecoveryDirectory(directory.Path);
        Write(source, "first.json", "first");
        Write(source, "nested/remaining.bak", "remaining");
        Write(source, "nested/moved.tmp", "moved");
        Directory.CreateDirectory(Path.Combine(destination, "nested"));
        // simulate an interruption after some files moved, before verification and cleanup.
        File.Move(Path.Combine(source, "first.json"), Path.Combine(destination, "first.json"));
        File.Move(Path.Combine(source, "nested/moved.tmp"), Path.Combine(destination, "nested/moved.tmp"));
        Directory.CreateDirectory(Path.Combine(source, "already-empty"));
        Directory.CreateDirectory(Path.Combine(destination, "already-empty"));

        MiniPadRecoveryMigration.Migrate(directory.Path);
        Write(source, "reintroduced.json", "older application content");
        MiniPadRecoveryMigration.Migrate(directory.Path);

        Assert.IsFalse(Directory.Exists(source));
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(destination, "first.json")));
        Assert.AreEqual("moved", File.ReadAllText(Path.Combine(destination, "nested/moved.tmp")));
        Assert.AreEqual("remaining", File.ReadAllText(Path.Combine(destination, "nested/remaining.bak")));
        Assert.AreEqual("older application content", File.ReadAllText(Path.Combine(destination, "reintroduced.json")));
    }

    [TestMethod]
    [DataRow("same")]
    [DataRow("different")]
    public void FilenameConflictsBlockBeforeAnyMoveEvenWhenIdentical(string destinationContent)
    {
        using var directory = new TemporaryTestDirectory();
        var source = LegacyDirectory(directory);
        var destination = MiniPadStorage.GetRecoveryDirectory(directory.Path);
        Write(source, "uncontested.tmp", "must stay");
        Write(source, "nested/conflict.json", "same");
        Write(destination, "nested/conflict.json", destinationContent);

        var error = Assert.ThrowsExactly<MiniPadMigrationException>(() => MiniPadRecoveryMigration.Migrate(directory.Path));

        StringAssert.Contains(error.Message, Path.Combine(source, "nested", "conflict.json"));
        StringAssert.Contains(error.Message, destination);
        StringAssert.Contains(error.Message, "restart Noted");
        Assert.AreEqual("same", File.ReadAllText(Path.Combine(source, "nested/conflict.json")));
        Assert.AreEqual(destinationContent, File.ReadAllText(Path.Combine(destination, "nested/conflict.json")));
        Assert.IsTrue(File.Exists(Path.Combine(source, "uncontested.tmp")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "uncontested.tmp")));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FileDirectoryCollisionsPreserveBothEntries(bool sourceIsDirectory)
    {
        using var directory = new TemporaryTestDirectory();
        var source = LegacyDirectory(directory);
        var destination = MiniPadStorage.GetRecoveryDirectory(directory.Path);
        Write(sourceIsDirectory ? source : destination, "collision/child.tmp", "child");
        Write(sourceIsDirectory ? destination : source, "collision", "file");

        Assert.ThrowsExactly<MiniPadMigrationException>(() => MiniPadRecoveryMigration.Migrate(directory.Path));

        Assert.AreEqual("child", File.ReadAllText(Path.Combine(sourceIsDirectory ? source : destination, "collision/child.tmp")));
        Assert.AreEqual("file", File.ReadAllText(Path.Combine(sourceIsDirectory ? destination : source, "collision")));
    }

    [TestMethod]
    [DataRow("micro-scratchpads")]
    [DataRow("minipads")]
    public void FileAtRecoveryDirectoryBlocksMigration(string folder)
    {
        using var directory = new TemporaryTestDirectory();
        Write(directory.Path, "recovery/" + folder, "occupied");

        Assert.ThrowsExactly<MiniPadMigrationException>(() => MiniPadRecoveryMigration.Migrate(directory.Path));

        Assert.AreEqual("occupied", File.ReadAllText(directory.File("recovery", folder)));
    }

    [TestMethod]
    public void LockedFileIsPreservedAndRetrySucceedsAfterUnlock()
    {
        using var directory = new TemporaryTestDirectory();
        var source = LegacyDirectory(directory);
        Write(source, "locked.tmp", "locked content");
        using (var fileLock = new FileStream(Path.Combine(source, "locked.tmp"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsExactly<MiniPadMigrationException>(() => MiniPadRecoveryMigration.Migrate(directory.Path));
        Assert.IsTrue(File.Exists(Path.Combine(source, "locked.tmp")));

        MiniPadRecoveryMigration.Migrate(directory.Path);

        Assert.AreEqual("locked content", File.ReadAllText(Path.Combine(MiniPadStorage.GetRecoveryDirectory(directory.Path), "locked.tmp")));
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("destination")]
    [DataRow("ancestor")]
    public void DirectoryLinksBlockMigrationWithoutFollowingThem(string location)
    {
        using var directory = new TemporaryTestDirectory();
        var outside = directory.File("outside");
        Write(outside, "evidence.tmp", "untouched");
        var source = LegacyDirectory(directory);
        Write(source, "nested/source.tmp", "source");
        var link = location switch
        {
            "source" => Path.Combine(source, "link"),
            "destination" => Path.Combine(MiniPadStorage.GetRecoveryDirectory(directory.Path), "nested"),
            _ => directory.File("linked-app-data")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("Creating filesystem links is unavailable: " + ex.Message);
        }
        try
        {
            var error = Assert.ThrowsExactly<MiniPadMigrationException>(() =>
                MiniPadRecoveryMigration.Migrate(location == "ancestor" ? link : directory.Path));
            StringAssert.Contains(error.Message, "filesystem link");
            Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(outside, "evidence.tmp")));
            Assert.IsTrue(File.Exists(Path.Combine(source, "nested/source.tmp")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static string LegacyDirectory(TemporaryTestDirectory directory) =>
        directory.File("recovery", "micro-scratchpads");

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
