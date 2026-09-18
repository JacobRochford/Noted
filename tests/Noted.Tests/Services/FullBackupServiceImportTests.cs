using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

public sealed partial class FullBackupServiceTests
{
    [TestMethod]
    public void ImportPreviewRebasesStoredNotePathsIncludingBothEditorPanes()
    {
        using var sourceDirectory = new TemporaryTestDirectory();
        using var targetDirectory = new TemporaryTestDirectory();
        var source = CreateSourceData(sourceDirectory);
        var sourceNote = Path.Combine(source.Notes, "one.txt");
        var sourceSecondaryNote = Path.Combine(source.Notes, "split-secondary.txt");
        File.WriteAllText(sourceSecondaryNote, "secondary pane content");
        WriteJson(
            Path.Combine(source.AppData, "settings.json"),
            new { SchemaVersion = 1, Revision = 1, NotesDirectory = source.Notes });
        WriteJson(
            Path.Combine(source.AppData, "session", "editor-workspace.json"),
            new NoteEditorSession
            {
                Tabs =
                [
                    new NoteEditorTabState { FilePath = sourceNote },
                    new NoteEditorTabState { FilePath = sourceSecondaryNote }
                ],
                ActiveFilePath = sourceNote,
                SecondaryFilePath = sourceSecondaryNote
            });
        var sourceHash = GetNotePathHash(sourceNote);
        WriteJson(
            Path.Combine(source.AppData, "recovery", $"{sourceHash}.json"),
            new NoteRecoveryDraft(sourceNote, "unsaved text", DateTime.UtcNow));
        WriteJson(
            Path.Combine(source.AppData, "note-history", sourceHash, "history.json"),
            new
            {
                SchemaVersion = 1,
                NotePath = sourceNote,
                Content = "previous text",
                SavedUtc = DateTime.UtcNow
            });
        var sourceService = new FullBackupService(source.AppData, source.Notes);
        Assert.AreEqual(
            FullBackupStatus.Created,
            sourceService.CreateOrUpdateUserBackup().Status);
        var exportPath = sourceDirectory.File("portable.notedbackup");
        Assert.IsTrue(sourceService.ExportUserBackup(exportPath).Success);

        var target = CreateSourceData(targetDirectory);
        var targetService = new FullBackupService(target.AppData, target.Notes);
        var preview = targetService.GetBackupImportPreview(exportPath);

        Assert.IsTrue(preview.IsValid, preview.Error);
        var targetNote = Path.Combine(target.Notes, "one.txt");
        var targetHash = GetNotePathHash(targetNote);
        var settingsFile = preview.Files.Single(file =>
            file.LogicalPath.Equals("app/settings.json", StringComparison.OrdinalIgnoreCase));
        var settings = targetService.ReadBackupImportFile(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!,
            settingsFile);
        Assert.IsTrue(settings.Success, settings.Error);
        using (var document = JsonDocument.Parse(settings.Data!))
        {
            Assert.AreEqual(
                Path.GetFullPath(target.Notes),
                document.RootElement.GetProperty("NotesDirectory").GetString());
        }

        var sessionFile = preview.Files.Single(file =>
            file.LogicalPath.Equals(
                "app/session/editor-workspace.json",
                StringComparison.OrdinalIgnoreCase));
        var sessionContent = targetService.ReadBackupImportFile(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!,
            sessionFile);
        var session = JsonSerializer.Deserialize<NoteEditorSession>(sessionContent.Data!);
        Assert.IsNotNull(session);
        Assert.AreEqual(targetNote, session.ActiveFilePath);
        var targetSecondaryNote = Path.Combine(target.Notes, "split-secondary.txt");
        Assert.AreEqual(targetSecondaryNote, session.SecondaryFilePath);
        Assert.AreEqual(2, session.Tabs.Count);
        Assert.AreEqual(targetNote, session.Tabs[0].FilePath);
        Assert.AreEqual(targetSecondaryNote, session.Tabs[1].FilePath);

        var recoveryFile = preview.Files.Single(file =>
            file.LogicalPath.Equals(
                $"app/recovery/{targetHash}.json",
                StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(
            $"app/recovery/{sourceHash}.json",
            recoveryFile.SourceLogicalPath);
        var recoveryContent = targetService.ReadBackupImportFile(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!,
            recoveryFile);
        var draft = JsonSerializer.Deserialize<NoteRecoveryDraft>(recoveryContent.Data!);
        Assert.IsNotNull(draft);
        Assert.AreEqual(targetNote, draft.FilePath);

        var historyFile = preview.Files.Single(file =>
            file.LogicalPath.Equals(
                $"app/note-history/{targetHash}/history.json",
                StringComparison.OrdinalIgnoreCase));
        var historyContent = targetService.ReadBackupImportFile(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!,
            historyFile);
        using var history = JsonDocument.Parse(historyContent.Data!);
        Assert.AreEqual(
            targetNote,
            history.RootElement.GetProperty("NotePath").GetString());
    }

    [TestMethod]
    [DataRow("tab", "sibling-prefix")]
    [DataRow("active", "sibling-prefix")]
    [DataRow("secondary", "sibling-prefix")]
    [DataRow("tab", "parent-traversal")]
    [DataRow("active", "parent-traversal")]
    [DataRow("secondary", "parent-traversal")]
    [DataRow("tab", "different-drive")]
    [DataRow("active", "different-drive")]
    [DataRow("secondary", "different-drive")]
    public void ImportRejectsSessionPathsOutsideTheSourceNotesRoot(string field, string pathKind)
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var sourceNote = Path.Combine(paths.Notes, "one.txt");
        var otherDrive = Path.GetPathRoot(paths.Notes)!.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase)
            ? @"Y:\"
            : @"Z:\";
        // These are stored path strings only; no files are created outside the fixture.
        var outsidePath = pathKind switch
        {
            "sibling-prefix" => Path.Combine(paths.Notes + "-other", "outside.txt"),
            "parent-traversal" => Path.Combine(paths.Notes, "..", "outside.txt"),
            "different-drive" => Path.Combine(otherDrive, "outside.txt"),
            _ => throw new ArgumentOutOfRangeException(nameof(pathKind))
        };
        WriteJson(
            Path.Combine(paths.AppData, "session", "editor-workspace.json"),
            new NoteEditorSession
            {
                Tabs = [new NoteEditorTabState { FilePath = field == "tab" ? outsidePath : sourceNote }],
                ActiveFilePath = field == "active" ? outsidePath : sourceNote,
                SecondaryFilePath = field == "secondary" ? outsidePath : null
            });
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("external-session-path.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);

        var preview = service.GetBackupImportPreview(exportPath);

        Assert.IsFalse(preview.IsValid);
        StringAssert.Contains(preview.Error!, "outside the backup's notes folder");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void ImportNormalizesEmptyOptionalSessionPaths(string? optionalPath)
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        WriteJson(
            Path.Combine(paths.AppData, "session", "editor-workspace.json"),
            new NoteEditorSession
            {
                Tabs = [new NoteEditorTabState { FilePath = Path.Combine(paths.Notes, "one.txt") }],
                ActiveFilePath = optionalPath,
                SecondaryFilePath = optionalPath
            });
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("empty-session-paths.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);
        var preview = service.GetBackupImportPreview(exportPath);
        Assert.IsTrue(preview.IsValid, preview.Error);
        var sessionFile = preview.Files.Single(file =>
            file.LogicalPath.Equals("app/session/editor-workspace.json", StringComparison.OrdinalIgnoreCase));

        var content = service.ReadBackupImportFile(
            exportPath, preview.BackupId, preview.VerificationToken!, sessionFile);

        Assert.IsTrue(content.Success, content.Error);
        var session = JsonSerializer.Deserialize<NoteEditorSession>(content.Data!);
        Assert.IsNotNull(session);
        Assert.IsNull(session.ActiveFilePath);
        Assert.IsNull(session.SecondaryFilePath);
    }

    [TestMethod]
    public void ImportedRestoreLeavesTheUserBackupUnchanged()
    {
        using var sourceDirectory = new TemporaryTestDirectory();
        using var targetDirectory = new TemporaryTestDirectory();
        var source = CreateSourceData(sourceDirectory);
        File.WriteAllText(Path.Combine(source.Notes, "one.txt"), "imported note");
        var sourceService = new FullBackupService(source.AppData, source.Notes);
        Assert.AreEqual(
            FullBackupStatus.Created,
            sourceService.CreateOrUpdateUserBackup().Status);
        var exportPath = sourceDirectory.File("portable.notedbackup");
        Assert.IsTrue(sourceService.ExportUserBackup(exportPath).Success);
        File.WriteAllText(Path.Combine(source.Notes, "one.txt"), "source path must stay unchanged");

        var target = CreateSourceData(targetDirectory);
        File.WriteAllText(Path.Combine(target.Notes, "one.txt"), "user backup note");
        var targetService = new FullBackupService(target.AppData, target.Notes);
        Assert.AreEqual(
            FullBackupStatus.Created,
            targetService.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(Path.Combine(target.Notes, "one.txt"), "latest current note");
        var userBackupNote = Path.Combine(
            targetService.BackupDirectory,
            "protected",
            "files",
            "notes",
            "one.txt");
        var preview = targetService.GetBackupImportPreview(exportPath);
        Assert.IsTrue(preview.IsValid, preview.Error);

        var protectedCurrentData = targetService.CreateBeforeRestoreBackup();
        var scheduled = targetService.ScheduleBackupImportRestore(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!);
        var restoringService = new FullBackupService(target.AppData, target.Notes);
        var restored = restoringService.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Created, protectedCurrentData.Status);
        Assert.AreEqual(FullBackupStatus.RestoreScheduled, scheduled.Status);
        Assert.AreEqual(FullBackupStatus.Restored, restored.Status);
        Assert.AreEqual("imported note", File.ReadAllText(Path.Combine(target.Notes, "one.txt")));
        Assert.AreEqual(
            "source path must stay unchanged",
            File.ReadAllText(Path.Combine(source.Notes, "one.txt")));
        Assert.AreEqual("user backup note", File.ReadAllText(userBackupNote));
        Assert.AreEqual(
            "latest current note",
            File.ReadAllText(Path.Combine(
                protectedCurrentData.BackupPath!,
                "files",
                "notes",
                "one.txt")));
        using (var settings = JsonDocument.Parse(
                   File.ReadAllText(Path.Combine(target.AppData, "settings.json"))))
        {
            Assert.AreEqual(
                Path.GetFullPath(target.Notes),
                settings.RootElement.GetProperty("NotesDirectory").GetString());
        }
        Assert.IsTrue(File.Exists(exportPath));
        Assert.HasCount(
            0,
            Directory.GetDirectories(
                targetService.BackupDirectory,
                "pending-import-*",
                SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public void ImportStartupRemovesAnOrphanedPendingFolder()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        var pendingPath = Path.Combine(
            service.BackupDirectory,
            $"pending-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pendingPath);
        File.WriteAllText(Path.Combine(pendingPath, "temporary.txt"), "temporary import data");

        _ = new FullBackupService(paths.AppData, paths.Notes);

        Assert.IsFalse(Directory.Exists(pendingPath));
    }

    [TestMethod]
    public void ImportRejectsUnsafeArchivePathsBeforeWritingFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("unsafe.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);
        using (var archive = ZipFile.Open(exportPath, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(
                archive.CreateEntry("files/../outside.txt").Open());
            writer.Write("unsafe");
        }

        var preview = service.GetBackupImportPreview(exportPath);

        Assert.IsFalse(preview.IsValid);
        Assert.IsFalse(File.Exists(directory.File("outside.txt")));
        Assert.HasCount(
            0,
            Directory.GetDirectories(
                service.BackupDirectory,
                "*import*",
                SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public void ImportRequiresAnotherPreviewWhenArchiveChanges()
    {
        using var sourceDirectory = new TemporaryTestDirectory();
        using var targetDirectory = new TemporaryTestDirectory();
        var source = CreateSourceData(sourceDirectory);
        var sourceService = new FullBackupService(source.AppData, source.Notes);
        Assert.AreEqual(
            FullBackupStatus.Created,
            sourceService.CreateOrUpdateUserBackup().Status);
        var exportPath = sourceDirectory.File("changed.notedbackup");
        Assert.IsTrue(sourceService.ExportUserBackup(exportPath).Success);
        var target = CreateSourceData(targetDirectory);
        var targetService = new FullBackupService(target.AppData, target.Notes);
        var preview = targetService.GetBackupImportPreview(exportPath);
        Assert.IsTrue(preview.IsValid, preview.Error);
        ReplaceArchiveFile(
            exportPath,
            "notes/one.txt",
            Encoding.UTF8.GetBytes("changed after preview"));

        var result = targetService.ScheduleBackupImportRestore(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!);

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        Assert.IsFalse(
            Directory.Exists(targetService.BackupDirectory) &&
            Directory.EnumerateDirectories(
                    targetService.BackupDirectory,
                    "*import*",
                    SearchOption.TopDirectoryOnly)
                .Any());
    }

    [TestMethod]
    public void ImportRejectsInvalidJsonEvenWhenItsHashMatchesTheManifest()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("invalid-json.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);
        ReplaceArchiveFile(
            exportPath,
            "app/settings.json",
            Encoding.UTF8.GetBytes("{\"SchemaVersion\":999}"));

        var preview = service.GetBackupImportPreview(exportPath);

        Assert.IsFalse(preview.IsValid);
        StringAssert.Contains(preview.Error!, "schema is not supported");
    }

    [TestMethod]
    public void ImportRejectsRelativeSourceFolders()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("relative-root.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);
        UpdateArchiveManifest(exportPath, manifest => manifest["NotesRoot"] = "relative-notes");

        var preview = service.GetBackupImportPreview(exportPath);

        Assert.IsFalse(preview.IsValid);
        StringAssert.Contains(preview.Error!, "invalid original storage folder");
    }

    [TestMethod]
    public void ImportRejectsUnsupportedEditorSessionPaths()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        WriteJson(
            Path.Combine(paths.AppData, "session", "editor-workspace.json"),
            new NoteEditorSession
            {
                Tabs =
                [
                    new NoteEditorTabState
                    {
                        FilePath = Path.Combine(paths.Notes, "unsupported.exe")
                    }
                ]
            });
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("unsupported-session.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);

        var preview = service.GetBackupImportPreview(exportPath);

        Assert.IsFalse(preview.IsValid);
        StringAssert.Contains(preview.Error!, "not a supported full note path");
    }

    private static void ReplaceArchiveFile(
        string archivePath,
        string logicalPath,
        byte[] content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.IsNotNull(manifestEntry);
        JsonObject manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonNode.Parse(stream) as JsonObject
                ?? throw new AssertFailedException("The exported manifest is not a JSON object.");
        }

        var files = manifest["Entries"] as JsonArray
            ?? throw new AssertFailedException("The exported manifest has no file list.");
        var file = files
            .OfType<JsonObject>()
            .Single(item => string.Equals(
                item["LogicalPath"]?.GetValue<string>(),
                logicalPath,
                StringComparison.OrdinalIgnoreCase));
        file["Length"] = content.LongLength;
        file["Sha256"] = Convert.ToHexString(SHA256.HashData(content));

        var archiveFile = archive.GetEntry($"files/{logicalPath}");
        Assert.IsNotNull(archiveFile);
        archiveFile.Delete();
        using (var stream = archive.CreateEntry($"files/{logicalPath}").Open())
            stream.Write(content);

        manifestEntry.Delete();
        using var manifestStream = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static void UpdateArchiveManifest(
        string archivePath,
        Action<JsonObject> update)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.IsNotNull(manifestEntry);
        JsonObject manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonNode.Parse(stream) as JsonObject
                ?? throw new AssertFailedException("The exported manifest is not a JSON object.");
        }

        update(manifest);
        manifestEntry.Delete();
        using var manifestStream = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string GetNotePathHash(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }
}
