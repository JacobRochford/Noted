using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class FileSystemLinkRecoveryTests
{
    [TestMethod]
    [DataRow("recovery")]
    [DataRow("recovery/minipads")]
    [DataRow("session")]
    public void LinkedRecoveryRootsRejectReadsAndWrites(string relative)
    {
        using var directory = new TemporaryTestDirectory();
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.json");
        File.WriteAllText(sentinel, "sentinel");
        using var link = new TestFileSystemLink(directory.File("app", relative), outside);
        var notePath = directory.File("external", "one.txt");
        if (relative == "session")
        {
            var service = new NoteEditorSessionService(directory.File("app"));
            Assert.IsNotEmpty(service.Load().Issues);
            Assert.ThrowsExactly<IOException>(() => service.Save(new NoteEditorSession()));
        }
        else if (relative == "recovery/minipads")
        {
            var service = new MiniPadRecoveryService(directory.File("app"));
            Assert.IsNull(service.LoadDraft().Draft);
            Assert.ThrowsExactly<IOException>(() => service.SaveDraft("changed"));
        }
        else
        {
            var service = new NoteRecoveryService(directory.File("app"));
            Assert.IsNotEmpty(service.LoadDrafts().Issues);
            Assert.IsNull(service.LoadDraft(notePath).Draft);
            Assert.ThrowsExactly<IOException>(() => service.SaveDraft(notePath, "changed"));
            Assert.ThrowsExactly<IOException>(() => service.DeleteDraft(notePath));
        }
        Assert.AreEqual("sentinel", File.ReadAllText(sentinel));
        Assert.AreEqual(1, Directory.GetFiles(outside).Length);
    }

    [TestMethod]
    [DataRow("note", false)]
    [DataRow("note", true)]
    [DataRow("minipad", false)]
    [DataRow("minipad", true)]
    [DataRow("session", false)]
    [DataRow("session", true)]
    [DataRow("settings", false)]
    [DataRow("settings", true)]
    [DataRow("scratchpad", false)]
    [DataRow("scratchpad", true)]
    public void LinkedPrimaryAndBackupFilesAreUnavailableAndPreserved(string kind, bool backup)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notePath = directory.File("external", "one.txt");
        var primary = kind switch
        {
            "note" => Path.Combine(app, "recovery", NotePathIdentity.GetHash(Path.GetFullPath(notePath)) + ".json"),
            "minipad" => Path.Combine(MiniPadStorage.GetRecoveryDirectory(app), MiniPadRecoveryService.LegacyDraftId.ToString("N") + ".json"),
            "session" => Path.Combine(app, "session", PersistencePaths.EditorSessionFile),
            "scratchpad" => Path.Combine(app, PersistencePaths.ScratchpadFile),
            _ => Path.Combine(app, PersistencePaths.SettingsFile)
        };
        var sentinel = directory.File("sentinel.json");
        var json = kind switch
        {
            "note" => System.Text.Json.JsonSerializer.Serialize(new NoteRecoveryDraft(notePath, "sentinel", DateTime.UtcNow)),
            "minipad" => System.Text.Json.JsonSerializer.Serialize(new MiniPadRecoveryDraft(MiniPadRecoveryService.LegacyDraftId, "sentinel", DateTime.UtcNow)),
            "session" => System.Text.Json.JsonSerializer.Serialize(new NoteEditorSession()),
            _ => "{}"
        };
        File.WriteAllText(sentinel, json);
        using var link = new TestFileSystemLink(primary + (backup ? ".bak" : ""), sentinel, false);
        if (kind == "note")
        {
            var service = new NoteRecoveryService(app);
            Assert.IsNull(service.LoadDraft(notePath).Draft);
            Assert.ThrowsExactly<IOException>(() => service.SaveDraft(notePath, "changed"));
        }
        else if (kind == "minipad")
        {
            var service = new MiniPadRecoveryService(app);
            Assert.IsNull(service.LoadDraft().Draft);
            Assert.ThrowsExactly<IOException>(() => service.SaveDraft("changed"));
        }
        else if (kind == "session")
        {
            var service = new NoteEditorSessionService(app);
            Assert.IsNotEmpty(service.Load().Issues);
            Assert.ThrowsExactly<IOException>(() => service.Save(new NoteEditorSession()));
        }
        else if (kind == "scratchpad")
        {
            var service = new ScratchpadContentService(app);
            Assert.IsFalse(service.LoadContent().Success);
            Assert.IsFalse(service.SaveContent("changed").Success);
        }
        else
        {
            var service = new AppSettingsService(app);
            Assert.ThrowsExactly<SettingsPersistenceException>(() => service.LoadNotesDirectory());
        }
        Assert.AreEqual(json, File.ReadAllText(sentinel));
        Assert.IsTrue((File.GetAttributes(primary + (backup ? ".bak" : "")) & FileAttributes.ReparsePoint) != 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LinkedNoteHistoryRootOrHashDirectoryCannotWriteOrPruneExternalFiles(bool hashDirectory)
    {
        using var directory = new TemporaryTestDirectory();
        var notePath = directory.File("external", "one.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
        File.WriteAllText(notePath, "original");
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "old.json"), "sentinel");
        var history = directory.File("app", PersistencePaths.NoteHistoryDirectory);
        using var link = new TestFileSystemLink(hashDirectory ? Path.Combine(history, NotePathIdentity.GetHash(notePath)) : history, outside);
        var service = new NoteContentService(directory.File("app"), TimeProvider.System, 1);

        Assert.IsNotNull(service.Save(notePath, "changed"));

        Assert.AreEqual("changed", File.ReadAllText(notePath));
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(outside, "old.json")));
        Assert.AreEqual(1, Directory.GetFiles(outside).Length);
    }

    [TestMethod]
    public void LinkedSettingsHistoryCannotWriteOrPruneExternalFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var outside = directory.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "settings_old.json"), "{}");
        var service = new AppSettingsService(directory.File("app"), TimeProvider.System, TimeSpan.Zero, 1);
        service.SaveNotesDirectory(directory.File("initial-notes"));
        Directory.Delete(directory.File("app", PersistencePaths.SettingsHistoryDirectory), recursive: true);
        using var link = new TestFileSystemLink(directory.File("app", PersistencePaths.SettingsHistoryDirectory), outside);
        service.SaveNotesDirectory(directory.File("notes"));
        Assert.IsNotNull(service.LastPersistenceWarning);
        Assert.AreEqual("{}", File.ReadAllText(Path.Combine(outside, "settings_old.json")));
        Assert.AreEqual(1, Directory.GetFiles(outside).Length);
    }

    [TestMethod]
    public void ValidationDoesNotCreateMissingDestinationDirectories()
    {
        using var directory = new TemporaryTestDirectory();
        FileSystemLinkValidator.ValidatePath(directory.File("missing", "nested", "one.txt"));
        Assert.IsFalse(Directory.Exists(directory.File("missing")));
    }

    [TestMethod]
    public void DanglingFileLinkIsUnavailableRatherThanMissing()
    {
        using var directory = new TemporaryTestDirectory();
        var target = directory.File("target.json");
        File.WriteAllText(target, "{}");
        using var link = new TestFileSystemLink(directory.File("linked.json"), target, false);
        File.Delete(target);

        Assert.AreEqual(JsonFileReadStatus.Unavailable,
            JsonFileStore.Read<object>(directory.File("linked.json")).Status);
        Assert.ThrowsExactly<IOException>(() => JsonFileStore.Write(directory.File("linked.json"), new object()));
        Assert.IsFalse(File.Exists(target));
    }
}
