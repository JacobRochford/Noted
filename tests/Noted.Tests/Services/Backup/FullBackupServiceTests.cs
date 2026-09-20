using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed partial class FullBackupServiceTests
{
    [TestMethod]
    public void ExportWritesACompleteVerifiedBackupFile()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("noted-backup.notedbackup");

        var result = service.ExportUserBackup(exportPath);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(Path.GetFullPath(exportPath), result.ExportPath);
        Assert.IsNull(result.Warning);
        using var archive = ZipFile.OpenRead(exportPath);
        Assert.IsNotNull(archive.GetEntry("manifest.json"));
        Assert.IsNotNull(archive.GetEntry("files/app/settings.json"));
        var noteEntry = archive.GetEntry("files/notes/one.txt");
        Assert.IsNotNull(noteEntry);
        using var reader = new StreamReader(noteEntry.Open());
        Assert.AreEqual("note one", reader.ReadToEnd());
        Assert.HasCount(
            service.GetUserBackupInfo().FileCount + 1,
            archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList());
    }

    [TestMethod]
    public void ExportReplacesAnExistingFileOnlyAfterVerification()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("noted-backup.notedbackup");
        File.WriteAllText(exportPath, "old export");

        var result = service.ExportUserBackup(exportPath);

        Assert.IsTrue(result.Success);
        using var archive = ZipFile.OpenRead(exportPath);
        Assert.IsNotNull(archive.GetEntry("manifest.json"));
        Assert.HasCount(
            1,
            Directory.GetFiles(directory.Path, "noted-backup.notedbackup", SearchOption.TopDirectoryOnly));
        Assert.HasCount(
            0,
            Directory.GetFiles(directory.Path, ".noted-backup.notedbackup.*", SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public void DamagedBackupCannotBeExported()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(
            Path.Combine(service.BackupDirectory, "protected", "files", "notes", "one.txt"),
            "damaged backup");
        var exportPath = directory.File("noted-backup.notedbackup");

        var result = service.ExportUserBackup(exportPath);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(File.Exists(exportPath));
        Assert.HasCount(
            0,
            Directory.GetFiles(directory.Path, ".noted-backup.notedbackup.*", SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public void BackupCannotBeExportedIntoItsOwnStorageFolder()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = Path.Combine(service.BackupDirectory, "copy.notedbackup");

        var result = service.ExportUserBackup(exportPath);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(File.Exists(exportPath));
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
    }

    [TestMethod]
    public void BackupPreviewVerifiesFilesAndComparesRestoreLocations()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);

        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "changed note");
        File.Delete(Path.Combine(paths.Notes, ".archive", "archived.md"));
        var dictionaryPath = Path.Combine(paths.AppData, "dictionary.json");
        File.Delete(dictionaryPath);
        Directory.CreateDirectory(dictionaryPath);

        var preview = service.GetUserBackupPreview();

        Assert.IsTrue(preview.Exists);
        Assert.IsTrue(preview.IsValid);
        Assert.IsNull(preview.Error);
        Assert.AreEqual(
            BackupFileComparison.Changed,
            preview.Files.Single(file => file.LogicalPath == "notes/one.txt").Comparison);
        Assert.AreEqual(
            BackupFileComparison.Missing,
            preview.Files.Single(file => file.LogicalPath == "notes/.archive/archived.md").Comparison);
        Assert.AreEqual(
            BackupFileComparison.Unavailable,
            preview.Files.Single(file => file.LogicalPath == "app/dictionary.json").Comparison);
        Assert.AreEqual(
            BackupFileComparison.Unchanged,
            preview.Files.Single(file => file.LogicalPath == "app/settings.json").Comparison);
        Assert.AreEqual(
            2,
            preview.Files.Single(file => file.LogicalPath == "app/checklist.json").ItemCount);
    }

    [TestMethod]
    public void BackupFilePreviewReturnsVerifiedContentInItsStoredFormat()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);

        var preview = service.GetUserBackupPreview();
        var note = service.ReadUserBackupFile(
            preview.BackupId,
            preview.Files.Single(file => file.LogicalPath == "notes/one.txt"));
        var checklist = service.ReadUserBackupFile(
            preview.BackupId,
            preview.Files.Single(file => file.LogicalPath == "app/checklist.json"));
        var scratchpad = service.ReadUserBackupFile(
            preview.BackupId,
            preview.Files.Single(file => file.LogicalPath == "app/scratchpad.rtf"));

        Assert.IsTrue(note.Success);
        Assert.AreEqual(BackupContentFormat.Text, note.Format);
        Assert.AreEqual("note one", System.Text.Encoding.UTF8.GetString(note.Data!));
        Assert.IsTrue(checklist.Success);
        Assert.AreEqual(BackupContentFormat.Json, checklist.Format);
        Assert.HasCount(
            2,
            JsonSerializer.Deserialize<List<ChecklistItemState>>(checklist.Data!)!);
        Assert.IsTrue(scratchpad.Success);
        Assert.AreEqual(BackupContentFormat.RichText, scratchpad.Format);
    }

    [TestMethod]
    public void BackupFilePreviewRefusesContentAfterBackupIsChanged()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var preview = service.GetUserBackupPreview();
        var note = preview.Files.Single(file => file.LogicalPath == "notes/one.txt");
        File.WriteAllText(
            Path.Combine(service.BackupDirectory, "protected", "files", "notes", "one.txt"),
            "changed backup");

        var content = service.ReadUserBackupFile(preview.BackupId, note);

        Assert.IsFalse(content.Success);
        Assert.IsTrue(content.NeedsRecheck);
        Assert.IsNull(content.Data);
        StringAssert.Contains(content.Error, "could not be verified");
    }

    [TestMethod]
    public void BackupPreviewComparesNotesWithTheRestoreFolderFromTheBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var otherNotes = directory.File("other-notes");
        Directory.CreateDirectory(otherNotes);
        File.WriteAllText(Path.Combine(otherNotes, "one.txt"), "different folder");
        service.UpdateNotesDirectory(otherNotes);

        var preview = service.GetUserBackupPreview();

        Assert.AreEqual(
            BackupFileComparison.Unchanged,
            preview.Files.Single(file => file.LogicalPath == "notes/one.txt").Comparison);
    }

    [TestMethod]
    public void OpenPreviewRefusesFilesFromAReplacementBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        var preview = firstRun.GetUserBackupPreview();
        var oldNote = preview.Files.Single(file => file.LogicalPath == "notes/one.txt");
        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "replacement backup note");
        var secondRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, secondRun.CreateOrUpdateUserBackup().Status);

        var content = secondRun.ReadUserBackupFile(preview.BackupId, oldNote);

        Assert.IsFalse(content.Success);
        Assert.IsTrue(content.NeedsRecheck);
        StringAssert.Contains(content.Error, "changed after this preview was opened");
    }

    [TestMethod]
    public void BackupWithUnknownRestorePathIsInvalidBeforeRestoreStarts()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var backupPath = Path.Combine(service.BackupDirectory, "protected");
        var oldFilePath = Path.Combine(backupPath, "files", "notes", "one.txt");
        var newFilePath = Path.Combine(backupPath, "files", "other", "one.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(newFilePath)!);
        File.Move(oldFilePath, newFilePath);
        var manifestPath = Path.Combine(backupPath, "manifest.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace(
                "notes/one.txt",
                "other/one.txt",
                StringComparison.Ordinal));

        var info = service.GetUserBackupInfo();
        var restore = service.ScheduleUserBackupRestore();

        Assert.IsFalse(info.IsValid);
        Assert.AreEqual(FullBackupStatus.Blocked, restore.Status);
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    public void FirstUserBackupStoresVerifiedCurrentData()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        File.WriteAllText(Path.Combine(paths.AppData, "settings.json.bak"), "{broken backup");
        File.WriteAllText(Path.Combine(paths.AppData, "ignored.tmp"), "temporary");
        File.WriteAllText(Path.Combine(paths.AppData, "ignored.pending-test"), "pending");
        File.WriteAllText(Path.Combine(paths.AppData, "ignored.corrupt-test"), "corrupt");
        var service = new FullBackupService(paths.AppData, paths.Notes);

        var result = service.CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        Assert.AreEqual(FullBackupType.User, result.Type);
        var userBackupPath = Path.Combine(service.BackupDirectory, "protected");
        Assert.AreEqual("note one", File.ReadAllText(
            Path.Combine(userBackupPath, "files", "notes", "one.txt")));
        Assert.IsTrue(File.Exists(
            Path.Combine(userBackupPath, "files", "app", "settings.json")));
        Assert.IsTrue(File.Exists(Path.Combine(userBackupPath, "manifest.json")));
        Assert.HasCount(
            0,
            Directory.GetFiles(userBackupPath, "*.bak", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(userBackupPath, "*.tmp", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(userBackupPath, "*.pending-*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(userBackupPath, "*.corrupt-*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public void BackupFilesKeepTheVersionOneStorageNames()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);

        Assert.AreEqual(
            FullBackupStatus.Created,
            firstRun.CreateOrUpdateUserBackup().Status);

        var userManifestPath = Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "manifest.json");
        using (var userManifest = JsonDocument.Parse(File.ReadAllText(userManifestPath)))
        {
            var root = userManifest.RootElement;
            Assert.AreNotEqual(Guid.Empty, root.GetProperty("SnapshotId").GetGuid());
            Assert.AreEqual("Protected", root.GetProperty("Role").GetString());
            Assert.IsFalse(root.TryGetProperty("BackupId", out _));
            Assert.IsFalse(root.TryGetProperty("Type", out _));
        }

        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "note two");
        var secondRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, secondRun.CreateRecentBackup().Status);
        using (var recentManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                   secondRun.BackupDirectory,
                   "latest",
                   "manifest.json"))))
        {
            Assert.AreEqual(
                "Latest",
                recentManifest.RootElement.GetProperty("Role").GetString());
        }

        var restoreRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(
            FullBackupStatus.RestoreScheduled,
            restoreRun.ScheduleUserBackupRestore().Status);
        using (var restoreRequest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                   restoreRun.BackupDirectory,
                   "restore-request.json"))))
        {
            var root = restoreRequest.RootElement;
            Assert.AreNotEqual(Guid.Empty, root.GetProperty("SnapshotId").GetGuid());
            Assert.IsFalse(root.TryGetProperty("BackupId", out _));
        }

        Assert.AreEqual(
            FullBackupStatus.Restored,
            restoreRun.ApplyPendingUserBackupRestore().Status);
    }

    [TestMethod]
    public void UserBackupStaysUnchangedWhenRecentBackupUpdates()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        var userBackupNotePath = Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "files",
            "notes",
            "one.txt");
        var userBackupManifest = File.ReadAllText(Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "manifest.json"));

        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "note two");
        var secondRun = new FullBackupService(paths.AppData, paths.Notes);
        var secondResult = secondRun.CreateRecentBackup();
        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "note three");
        var thirdRun = new FullBackupService(paths.AppData, paths.Notes);
        var thirdResult = thirdRun.CreateRecentBackup();

        Assert.AreEqual(FullBackupType.Recent, secondResult.Type);
        Assert.AreEqual(FullBackupStatus.Created, thirdResult.Status);
        Assert.AreEqual("note one", File.ReadAllText(userBackupNotePath));
        Assert.AreEqual(
            userBackupManifest,
            File.ReadAllText(Path.Combine(
                thirdRun.BackupDirectory,
                "protected",
                "manifest.json")));
        Assert.AreEqual(
            "note three",
            File.ReadAllText(Path.Combine(
                thirdRun.BackupDirectory,
                "latest",
                "files",
                "notes",
                "one.txt")));
        Assert.HasCount(
            0,
            Directory.GetDirectories(thirdRun.BackupDirectory, ".replacing-*"));
    }

    [TestMethod]
    public void CreatesOnlyOneBackupPerRun()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "changed again");

        var secondResult = service.CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Skipped, secondResult.Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(service.BackupDirectory, "latest")));
    }

    [TestMethod]
    public void DamagedUserBackupBlocksRecentBackupWithoutChangingFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        firstRun.CreateOrUpdateUserBackup();
        var userBackupNotePath = Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "files",
            "notes",
            "one.txt");
        File.WriteAllText(userBackupNotePath, "tampered backup");
        var tamperedBytes = File.ReadAllBytes(userBackupNotePath);
        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "new source text");

        var result = new FullBackupService(paths.AppData, paths.Notes).CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        CollectionAssert.AreEqual(tamperedBytes, File.ReadAllBytes(userBackupNotePath));
        Assert.IsFalse(Directory.Exists(Path.Combine(firstRun.BackupDirectory, "latest")));
    }

    [TestMethod]
    public void UnexpectedFileInUserBackupBlocksRecentBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        firstRun.CreateOrUpdateUserBackup();
        var unexpectedPath = Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "unexpected.txt");
        File.WriteAllText(unexpectedPath, "do not remove");

        var result = new FullBackupService(paths.AppData, paths.Notes).CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        Assert.AreEqual("do not remove", File.ReadAllText(unexpectedPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(firstRun.BackupDirectory, "latest")));
    }

    [TestMethod]
    public void EmptyChecklistDoesNotReplaceRecentBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        firstRun.CreateOrUpdateUserBackup();
        WriteJson(
            Path.Combine(paths.AppData, "checklist.json"),
            new List<ChecklistItemState>());

        var result = new FullBackupService(paths.AppData, paths.Notes).CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        StringAssert.Contains(result.Message, "empty collection");
        Assert.IsFalse(Directory.Exists(Path.Combine(firstRun.BackupDirectory, "latest")));
    }

    [TestMethod]
    public void EmptyDictionaryDoesNotReplaceTheUserBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        WriteJson(
            Path.Combine(paths.AppData, "dictionary.json"),
            new List<DictionaryItemState>());

        var result = new FullBackupService(paths.AppData, paths.Notes)
            .CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        var userBackupDictionary = JsonSerializer.Deserialize<List<DictionaryItemState>>(
            File.ReadAllText(Path.Combine(
                firstRun.BackupDirectory,
                "protected",
                "files",
                "app",
                "dictionary.json")))!;
        Assert.HasCount(1, userBackupDictionary);
    }

    [TestMethod]
    public void InvalidCurrentDataDoesNotCreateBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        File.WriteAllText(Path.Combine(paths.AppData, "dictionary.json"), "{broken");
        var service = new FullBackupService(paths.AppData, paths.Notes);

        var result = service.CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Failed, result.Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(service.BackupDirectory, "protected")));
        Assert.IsFalse(Directory.Exists(Path.Combine(service.BackupDirectory, "latest")));
    }

    [TestMethod]
    public void UnfinishedBackupFolderBlocksAnotherBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        var unfinishedPath = Path.Combine(
            service.BackupDirectory,
            ".building-manual-review");
        Directory.CreateDirectory(unfinishedPath);
        File.WriteAllText(Path.Combine(unfinishedPath, "evidence.txt"), "keep me");

        var result = service.CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(unfinishedPath, "evidence.txt")));
    }

    [TestMethod]
    public void BackupDoesNotCopyItsOwnFolder()
    {
        using var directory = new TemporaryTestDirectory();
        var sharedDirectory = directory.File("shared");
        Directory.CreateDirectory(sharedDirectory);
        File.WriteAllText(Path.Combine(sharedDirectory, "one.txt"), "first");
        File.WriteAllText(
            Path.Combine(sharedDirectory, "settings.json"),
            "{\"SchemaVersion\":1,\"Revision\":1}");
        var firstRun = new FullBackupService(sharedDirectory, sharedDirectory);
        firstRun.CreateOrUpdateUserBackup();
        File.WriteAllText(Path.Combine(sharedDirectory, "one.txt"), "second");

        var secondRun = new FullBackupService(sharedDirectory, sharedDirectory);
        var result = secondRun.CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        Assert.HasCount(
            1,
            Directory.GetFiles(
                Path.Combine(secondRun.BackupDirectory, "latest", "files", "notes"),
                "*.txt",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    public void RecentBackupRequiresUserBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);

        var result = service.CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(service.BackupDirectory, "protected")));
        Assert.IsFalse(Directory.Exists(Path.Combine(service.BackupDirectory, "latest")));
    }

    [TestMethod]
    public void UserBackupCanBeUpdatedAndKeepsThePreviousVersion()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "changed");

        var result = new FullBackupService(paths.AppData, paths.Notes)
            .CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        Assert.AreEqual(
            "changed",
            File.ReadAllText(Path.Combine(
                firstRun.BackupDirectory,
                "protected",
                "files",
                "notes",
                "one.txt")));
        Assert.AreEqual(
            "note one",
            File.ReadAllText(Path.Combine(
                firstRun.BackupDirectory,
                "previous",
                "files",
                "notes",
                "one.txt")));
    }

    [TestMethod]
    public void RestoreReplacesCurrentFilesWithoutChangingTheUserBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        var backupNotePath = Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "files",
            "notes",
            "one.txt");
        var backupBytes = File.ReadAllBytes(backupNotePath);

        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "damaged note");
        WriteJson(
            Path.Combine(paths.AppData, "dictionary.json"),
            new List<DictionaryItemState>());
        var restoreRun = new FullBackupService(paths.AppData, paths.Notes);

        var scheduled = restoreRun.ScheduleUserBackupRestore();
        var restored = restoreRun.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.RestoreScheduled, scheduled.Status);
        Assert.AreEqual(FullBackupStatus.Restored, restored.Status);
        Assert.AreEqual("note one", File.ReadAllText(Path.Combine(paths.Notes, "one.txt")));
        Assert.HasCount(
            1,
            JsonSerializer.Deserialize<List<DictionaryItemState>>(
                File.ReadAllText(Path.Combine(paths.AppData, "dictionary.json")))!);
        CollectionAssert.AreEqual(backupBytes, File.ReadAllBytes(backupNotePath));
        Assert.IsFalse(File.Exists(Path.Combine(
            restoreRun.BackupDirectory,
            "restore-request.json")));
    }

    [TestMethod]
    public void InterruptedRestoreCanBeRetriedFromTheUnchangedBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        var backupManifestPath = Path.Combine(
            firstRun.BackupDirectory,
            "protected",
            "manifest.json");
        var backupManifest = File.ReadAllBytes(backupManifestPath);
        var dictionaryPath = Path.Combine(paths.AppData, "dictionary.json");
        File.Delete(dictionaryPath);
        Directory.CreateDirectory(dictionaryPath);

        var restoreRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(
            FullBackupStatus.RestoreScheduled,
            restoreRun.ScheduleUserBackupRestore().Status);

        var failed = restoreRun.ApplyPendingUserBackupRestore();
        Directory.Delete(dictionaryPath);
        var retried = restoreRun.ApplyPendingUserBackupRestore();

        Assert.AreEqual(FullBackupStatus.Failed, failed.Status);
        Assert.AreEqual(FullBackupStatus.Restored, retried.Status);
        CollectionAssert.AreEqual(backupManifest, File.ReadAllBytes(backupManifestPath));
        Assert.HasCount(
            1,
            JsonSerializer.Deserialize<List<DictionaryItemState>>(
                File.ReadAllText(dictionaryPath))!);
    }

    [TestMethod]
    public void RecentBackupWaitsTwentyFourHoursAfterLastBackup()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var firstRun = new FullBackupService(paths.AppData, paths.Notes, clock);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        File.WriteAllText(Path.Combine(paths.Notes, "one.txt"), "changed");

        clock.Advance(TimeSpan.FromHours(23));
        var earlyResult = new FullBackupService(paths.AppData, paths.Notes, clock)
            .CreateRecentBackupIfDue(TimeSpan.FromHours(24));
        clock.Advance(TimeSpan.FromHours(1));
        var dueResult = new FullBackupService(paths.AppData, paths.Notes, clock)
            .CreateRecentBackupIfDue(TimeSpan.FromHours(24));

        Assert.AreEqual(FullBackupStatus.Skipped, earlyResult.Status);
        Assert.AreEqual(FullBackupStatus.Created, dueResult.Status);
        Assert.AreEqual(FullBackupType.Recent, dueResult.Type);
    }

    [TestMethod]
    public void BackupUsesUpdatedNotesDirectory()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var firstRun = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);
        var newNotes = directory.File("new-notes");
        Directory.CreateDirectory(newNotes);
        File.WriteAllText(Path.Combine(newNotes, "moved.txt"), "new folder note");
        var secondRun = new FullBackupService(paths.AppData, paths.Notes);

        secondRun.UpdateNotesDirectory(newNotes);
        var result = secondRun.CreateRecentBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        Assert.AreEqual(
            "new folder note",
            File.ReadAllText(Path.Combine(
                secondRun.BackupDirectory,
                "latest",
                "files",
                "notes",
                "moved.txt")));
    }

    [TestMethod]
    public void BackupCopiesCurrentMiniPadDraftWithoutChangingOldFiles()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var miniPadDirectory = Path.Combine(
            paths.AppData,
            "recovery",
            "micro-scratchpads");
        var archiveDirectory = Path.Combine(miniPadDirectory, "archive");
        Directory.CreateDirectory(archiveDirectory);
        var singletonId = new Guid("A6CB4208-79C0-4C08-9D07-9CDF33F31337");
        var singletonPath = Path.Combine(miniPadDirectory, $"{singletonId:N}.json");
        WriteJson(
            singletonPath,
            new MiniPadRecoveryDraft(singletonId, "active MiniPad", DateTime.UtcNow));
        var legacyId = Guid.NewGuid();
        var legacyPath = Path.Combine(miniPadDirectory, $"{legacyId:N}.json");
        WriteJson(
            legacyPath,
            new MiniPadRecoveryDraft(legacyId, "legacy MiniPad", DateTime.UtcNow));
        var legacyBytes = File.ReadAllBytes(legacyPath);
        var archivedPath = Path.Combine(archiveDirectory, "old.json");
        File.WriteAllText(archivedPath, "{broken archived recovery");
        var temporaryPath = Path.Combine(miniPadDirectory, ".old.json.test.tmp");
        File.WriteAllText(temporaryPath, "temporary evidence");
        var service = new FullBackupService(paths.AppData, paths.Notes);

        var result = service.CreateOrUpdateUserBackup();

        Assert.AreEqual(FullBackupStatus.Created, result.Status);
        var backupMiniPadDirectory = Path.Combine(
            service.BackupDirectory,
            "protected",
            "files",
            "app",
            "recovery",
            "micro-scratchpads");
        Assert.AreEqual(
            "active MiniPad",
            JsonSerializer.Deserialize<MiniPadRecoveryDraft>(
                File.ReadAllText(Path.Combine(backupMiniPadDirectory, $"{singletonId:N}.json")))!.Content);
        Assert.HasCount(
            1,
            Directory.GetFiles(backupMiniPadDirectory, "*.json", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(legacyBytes, File.ReadAllBytes(legacyPath));
        Assert.AreEqual("{broken archived recovery", File.ReadAllText(archivedPath));
        Assert.AreEqual("temporary evidence", File.ReadAllText(temporaryPath));
    }

    private static BackupTestPaths CreateSourceData(TemporaryTestDirectory directory)
    {
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "one.txt"), "note one");
        Directory.CreateDirectory(Path.Combine(notes, ".archive"));
        File.WriteAllText(Path.Combine(notes, ".archive", "archived.md"), "archived note");
        File.WriteAllText(
            Path.Combine(appData, "settings.json"),
            "{\"SchemaVersion\":1,\"Revision\":1}");
        WriteJson(
            Path.Combine(appData, "checklist.json"),
            new List<ChecklistItemState>
            {
                new() { Text = "first" },
                new() { Text = "second" }
            });
        WriteJson(
            Path.Combine(appData, "checklist-tabs.json"),
            new List<ChecklistTabState>
            {
                new() { Id = "all", Name = "All" }
            });
        WriteJson(
            Path.Combine(appData, "dictionary.json"),
            new List<DictionaryItemState>
            {
                new() { Word = "atomic", Description = "all at once" }
            });
        File.WriteAllText(Path.Combine(appData, "scratchpad.rtf"), "{\\rtf1 scratchpad}");
        return new BackupTestPaths(appData, notes);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    private sealed record BackupTestPaths(string AppData, string Notes);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
