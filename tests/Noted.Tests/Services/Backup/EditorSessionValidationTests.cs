using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class EditorSessionValidationTests
{
    [TestMethod]
    [DataRow("{\"Tabs\":null}")]
    [DataRow("{\"Tabs\":[null]}")]
    public void MalformedPrimaryUsesBackupAndPreservesBothFiles(string json)
    {
        using var directory = new TemporaryTestDirectory();
        var service = new NoteEditorSessionService(directory.Path);
        var valid = new NoteEditorSession { Tabs = [new NoteEditorTabState { FilePath = directory.File("one.txt") }] };
        service.Save(valid);
        service.Save(valid with { ActiveFilePath = directory.File("one.txt") });
        var primary = directory.File("session", "editor-workspace.json");
        var backup = primary + ".bak";
        var previous = File.ReadAllBytes(backup);
        File.WriteAllText(primary, json);

        var recovered = new NoteEditorSessionService(directory.Path);
        var result = recovered.Load();
        Assert.HasCount(1, result.Session.Tabs);
        Assert.HasCount(1, Directory.GetFiles(directory.File("session"), "*.corrupt-*"));
        recovered.Save(result.Session);
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(backup));
    }

    [TestMethod]
    public void InvalidSaveIsRejectedBeforeReplacingValidSession()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new NoteEditorSessionService(directory.Path);
        service.Save(new NoteEditorSession());
        var bytes = File.ReadAllBytes(directory.File("session", "editor-workspace.json"));
        Assert.ThrowsExactly<JsonException>(() => service.Save(new NoteEditorSession { Tabs = [null!] }));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(directory.File("session", "editor-workspace.json")));
        Assert.IsFalse(File.Exists(directory.File("session", "editor-workspace.json.bak")));
    }

    [TestMethod]
    public void BothMalformedCopiesArePreservedAndReturnSafeEmptySession()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("session"));
        File.WriteAllText(directory.File("session", "editor-workspace.json"), "{\"Tabs\":[null]}");
        File.WriteAllText(directory.File("session", "editor-workspace.json.bak"), "{\"Tabs\":null}");
        var result = new NoteEditorSessionService(directory.Path).Load();
        Assert.HasCount(0, result.Session.Tabs);
        Assert.HasCount(2, Directory.GetFiles(directory.File("session"), "*.corrupt-*"));
    }

    [TestMethod]
    public void UnavailableRecoveryBlocksSessionWrites()
    {
        using var directory = new TemporaryTestDirectory();
        Directory.CreateDirectory(directory.File("session"));
        var primary = directory.File("session", "editor-workspace.json");
        File.WriteAllText(primary, "{\"Tabs\":[null]}");
        File.WriteAllText(primary + ".bak", "{\"Tabs\":[]}");
        using var fileLock = new FileStream(primary + ".bak", FileMode.Open, FileAccess.Read, FileShare.None);
        var service = new NoteEditorSessionService(directory.Path);
        Assert.HasCount(0, service.Load().Session.Tabs);
        Assert.ThrowsExactly<IOException>(() => service.Save(new NoteEditorSession()));
    }

    [TestMethod]
    [DataRow("{\"Tabs\":null}")]
    [DataRow("{\"Tabs\":[null]}")]
    public void CollectionAndImportRejectTheSameMalformedSession(string json)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(Path.Combine(app, "session"));
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(app, "session", "editor-workspace.json"), json);
        var catalog = new BackupContentCatalog(app, notes, Path.Combine(app, "full-snapshots"));
        Assert.ThrowsExactly<JsonException>(() => catalog.Collect());

        var archive = directory.File("malformed.notedbackup");
        WriteArchive(archive, app, notes, json);
        var service = new FullBackupService(app, notes);
        var preview = service.GetBackupImportPreview(archive);
        Assert.IsFalse(preview.IsValid);
        Assert.IsFalse(Directory.Exists(service.BackupDirectory));
    }

    [TestMethod]
    public void MalformedLocalSnapshotStopsRestoreBeforeChangingNotes()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "one.txt"), "new text");
        var archive = directory.File("malformed.notedbackup");
        WriteArchive(archive, app, notes, "{\"Tabs\":[null]}");
        var service = new FullBackupService(app, notes);
        var snapshot = Path.Combine(service.BackupDirectory, "protected");
        ZipFile.ExtractToDirectory(archive, snapshot);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(snapshot, "manifest.json")));
        var request = Path.Combine(service.BackupDirectory, "restore-request.json");
        JsonFileStore.Write(request, new FullBackupRestoreRequest(1,
            manifest.RootElement.GetProperty("SnapshotId").GetGuid(), DateTime.UtcNow), options: BackupFormat.JsonOptions);
        Assert.AreEqual(FullBackupStatus.Failed, service.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual("new text", File.ReadAllText(Path.Combine(notes, "one.txt")));
        Assert.IsTrue(File.Exists(request));
    }

    [TestMethod]
    public void HistoricalBooleanSessionStillLoadsAndImports()
    {
        using var directory = new TemporaryTestDirectory();
        var json = """{"Tabs":[{"FilePath":"C:\\Notes\\one.md","MarkdownPreviewEnabled":true}]}""";
        var session = BackupContentCatalog.ValidateEditorSession(Encoding.UTF8.GetBytes(json));
        Assert.AreEqual(MarkdownPreviewMode.Preview, session.Tabs.Single().MarkdownPreviewMode);
        var archive = directory.File("historical.notedbackup");
        WriteArchive(archive, "C:\\OldApp", "C:\\Notes", json);
        Directory.CreateDirectory(directory.File("notes"));
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        var preview = service.GetBackupImportPreview(archive);
        Assert.IsTrue(preview.IsValid, preview.Error);
        var content = service.ReadBackupImportFile(archive, preview.BackupId, preview.VerificationToken!, preview.Files.Single());
        Assert.IsTrue(content.Success, content.Error);
        var imported = BackupContentCatalog.ValidateEditorSession(content.Data!);
        Assert.AreEqual(MarkdownPreviewMode.Preview, imported.Tabs.Single().MarkdownPreviewMode);
        Assert.AreEqual(directory.File("notes", "one.md"), imported.Tabs.Single().FilePath);
    }

    private static void WriteArchive(string path, string app, string notes, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var stream = archive.CreateEntry("files/app/session/editor-workspace.json").Open())
            stream.Write(bytes);
        var manifest = new
        {
            SchemaVersion = 1,
            SnapshotId = Guid.NewGuid(),
            Role = "Protected",
            CreatedUtc = DateTime.UtcNow,
            AppDataRoot = app,
            NotesRoot = notes,
            Entries = new[] { new
            {
                LogicalPath = "app/session/editor-workspace.json",
                OriginalPath = Path.Combine(app, "session", "editor-workspace.json"),
                Length = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                ItemCount = (int?)null,
                ContentLength = (long?)null
            } }
        };
        using var manifestStream = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifestStream, manifest);
    }
}
