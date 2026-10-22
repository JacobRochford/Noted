using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

public sealed partial class FullBackupServiceTests
{
    [TestMethod]
    [DataRow(".json", 16 * 1024 * 1024, true)]
    [DataRow(".JSON", 16 * 1024 * 1024 + 1, false)]
    [DataRow(".txt", 16 * 1024 * 1024 + 1, true)]
    public void ExportUsesImportPayloadLimitsWithoutChangingLocalEligibility(string extension, int size, bool eligible)
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var note = Path.Combine(paths.Notes, "large" + extension);
        var bytes = new byte[size];
        Array.Fill(bytes, (byte)' ');
        bytes[0] = (byte)'[';
        bytes[1] = (byte)']';
        File.WriteAllBytes(note, bytes);
        BackupTestSession.Save(paths.AppData, note);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var backupPath = Path.Combine(service.BackupDirectory, "protected");
        var before = FingerprintExportFixture(backupPath);
        var destination = directory.File("portable.notedbackup");
        File.WriteAllText(destination, "existing export");

        var result = service.ExportUserBackup(destination);

        Assert.AreEqual(eligible, result.Success, result.Message);
        if (eligible)
        {
            Assert.IsTrue(service.GetBackupImportPreview(destination).IsValid);
            using var archive = ZipFile.OpenRead(destination);
            var entry = archive.GetEntry("files/notes/large" + extension);
            Assert.IsNotNull(entry);
            Assert.AreEqual((long)size, entry.Length);
        }
        else
        {
            StringAssert.Contains(result.Message, "notes/large.JSON");
            StringAssert.Contains(result.Message, "JSON import limit of 16 MiB");
            Assert.AreEqual("existing export", File.ReadAllText(destination));
        }
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
        CollectionAssert.AreEqual(before, FingerprintExportFixture(backupPath));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, ".portable.notedbackup.*"));
        Assert.HasCount(0, Directory.GetDirectories(service.BackupDirectory, "*import*"));
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    [DataRow("settings")]
    [DataRow("malformed-json")]
    [DataRow("history-identity")]
    [DataRow("external-session")]
    [DataRow("external-recovery")]
    [DataRow("external-history")]
    [DataRow("unsupported-file")]
    public void ExportRejectsImportIneligibleContentAndPreservesBothBackups(string problem)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        var note = Path.Combine(notes, "one.txt");
        var external = directory.File("outside.txt");
        var logicalPath = "app/settings.json";
        byte[] bytes;
        switch (problem)
        {
            case "settings":
                bytes = Encoding.UTF8.GetBytes("{\"SchemaVersion\":999}");
                break;
            case "malformed-json":
                bytes = Encoding.UTF8.GetBytes("{broken");
                break;
            case "external-session":
                logicalPath = "app/session/editor-workspace.json";
                bytes = JsonSerializer.SerializeToUtf8Bytes(new NoteEditorSession
                {
                    Tabs = [new NoteEditorTabState { FilePath = external }]
                });
                break;
            case "external-recovery":
                logicalPath = $"app/recovery/{GetNotePathHash(external)}.json";
                bytes = JsonSerializer.SerializeToUtf8Bytes(new NoteRecoveryDraft(external, "unsaved", DateTime.UtcNow));
                break;
            case "history-identity":
            case "external-history":
                var historyNote = problem == "external-history" ? external : note;
                var hash = problem == "history-identity" ? "wrong-identity" : GetNotePathHash(historyNote);
                logicalPath = $"app/note-history/{hash}/entry.json";
                bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    SchemaVersion = 1, NotePath = historyNote, Content = "previous", SavedUtc = DateTime.UtcNow
                });
                break;
            case "unsupported-file":
                logicalPath = "app/unknown.txt";
                bytes = Encoding.UTF8.GetBytes("unsupported");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(problem));
        }
        var service = new FullBackupService(app, notes);
        var backupPath = Path.Combine(service.BackupDirectory, "protected");
        WriteExportFixture(backupPath, app, notes, new() { [logicalPath] = bytes });
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
        var before = FingerprintExportFixture(backupPath);
        var independentArchive = directory.File("ineligible.notedbackup");
        ZipFile.CreateFromDirectory(backupPath, independentArchive);
        Assert.IsFalse(service.GetBackupImportPreview(independentArchive).IsValid);
        var destination = directory.File("portable.notedbackup");
        File.WriteAllText(destination, "existing export");

        var result = service.ExportUserBackup(destination);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, logicalPath);
        if (problem.StartsWith("external-", StringComparison.Ordinal))
            StringAssert.Contains(result.Message, "outside the backup's notes folder");
        if (problem is "settings" or "malformed-json")
        {
            StringAssert.Contains(result.Message, "invalid or unsupported feature data");
            Assert.IsFalse(result.Message.Contains("BytePositionInLine", StringComparison.Ordinal));
        }
        if (problem == "history-identity")
            StringAssert.Contains(result.Message, "history path does not match its note");
        Assert.AreEqual("existing export", File.ReadAllText(destination));
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
        CollectionAssert.AreEqual(before, FingerprintExportFixture(backupPath));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, ".portable.notedbackup.*"));
        Assert.HasCount(0, Directory.GetDirectories(service.BackupDirectory, "*import*"));
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    public void ExportChecksUncompressedManifestSizeWithoutInvalidatingLocalBackup(int extraBytes, bool eligible)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        var service = new FullBackupService(app, notes);
        var backupPath = Path.Combine(service.BackupDirectory, "protected");
        WriteExportFixture(backupPath, app, notes, new() { ["notes/one.txt"] = [1] });
        var manifestPath = Path.Combine(backupPath, "manifest.json");
        var manifest = File.ReadAllBytes(manifestPath);
        var padded = Enumerable.Repeat((byte)' ', 4 * 1024 * 1024 + extraBytes).ToArray();
        manifest.CopyTo(padded, 0);
        File.WriteAllBytes(manifestPath, padded);
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
        var before = FingerprintExportFixture(backupPath);
        var destination = directory.File("portable.notedbackup");
        File.WriteAllText(destination, "existing export");

        var result = service.ExportUserBackup(destination);

        Assert.AreEqual(eligible, result.Success, result.Message);
        if (eligible)
            Assert.IsTrue(service.GetBackupImportPreview(destination).IsValid);
        else
        {
            StringAssert.Contains(result.Message, "manifest exceeds the 4 MiB import limit");
            Assert.AreEqual("existing export", File.ReadAllText(destination));
        }
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
        CollectionAssert.AreEqual(before, FingerprintExportFixture(backupPath));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, ".portable.notedbackup.*"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FinalExportVerificationFailureRestoresExistingDestinationAndPreservesFailedFile(bool existing)
    {
        using var directory = new TemporaryTestDirectory();
        var temp = directory.File("export.tmp");
        var destination = directory.File("portable.notedbackup");
        File.WriteAllText(temp, "new export");
        if (existing)
            File.WriteAllText(destination, "existing export");
        var verifiedPaths = new List<string>();

        Assert.ThrowsExactly<InvalidDataException>(() => FullBackupService.PublishBackupExport(temp, destination, path =>
        {
            verifiedPaths.Add(path);
            Assert.AreEqual("new export", File.ReadAllText(path));
            if (path == destination)
                throw new InvalidDataException("Final compatibility verification failed.");
        }));

        CollectionAssert.AreEqual(new[] { temp, destination }, verifiedPaths);
        Assert.IsFalse(File.Exists(temp));
        Assert.AreEqual(existing, File.Exists(destination));
        if (existing)
            Assert.AreEqual("existing export", File.ReadAllText(destination));
        var failed = Directory.GetFiles(directory.Path, ".portable.notedbackup.*.failed");
        Assert.HasCount(1, failed);
        Assert.AreEqual("new export", File.ReadAllText(failed[0]));
        Assert.HasCount(0, Directory.GetFiles(directory.Path, "*.rollback"));
    }

    [TestMethod]
    public void ExportDoesNotExposeUnrelatedFileSystemExceptionDetails()
    {
        using var directory = new TemporaryTestDirectory();
        var paths = CreateSourceData(directory);
        var service = new FullBackupService(paths.AppData, paths.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var destination = directory.File("occupied.notedbackup");
        Directory.CreateDirectory(destination);

        var result = service.ExportUserBackup(destination);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("The backup could not be exported.", result.Message);
        Assert.IsTrue(Directory.Exists(destination));
        Assert.IsTrue(service.GetUserBackupInfo().IsValid);
        Assert.HasCount(0, Directory.GetFiles(directory.Path, ".occupied.notedbackup.*"));
    }

    private static void WriteExportFixture(string backupPath, string app, string notes, Dictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(notes);
        foreach (var (logicalPath, bytes) in files)
        {
            var path = Path.Combine(backupPath, "files", logicalPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        // literal historical fields keep fixture eligibility independent of the exporter.
        var manifest = new
        {
            SchemaVersion = 1, SnapshotId = Guid.NewGuid(), Role = "Protected", CreatedUtc = DateTime.UtcNow,
            AppDataRoot = app, NotesRoot = notes,
            Entries = files.Select(file => new
            {
                LogicalPath = file.Key, OriginalPath = Path.Combine(app, file.Key[4..]),
                Length = file.Value.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(file.Value)),
                ItemCount = (int?)null, ContentLength = (long?)null
            })
        };
        File.WriteAllBytes(Path.Combine(backupPath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static string[] FingerprintExportFixture(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(stream));
            })
            .ToArray();
}
