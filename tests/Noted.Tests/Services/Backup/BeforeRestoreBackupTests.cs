using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BeforeRestoreBackupTests
{
    [TestMethod]
    public void EmptyStorageIsNotCreatedByBrowsing()
    {
        using var fixture = new Fixture();
        var result = fixture.Service.GetBeforeRestoreBackups();
        Assert.IsNull(result.Error);
        Assert.HasCount(0, result.Backups);
        Assert.IsFalse(Directory.Exists(fixture.Service.BackupDirectory));
    }

    [TestMethod]
    public void DiscoveryOrdersImmediateSnapshotsAndIsolatesInvalidOnesWithoutTrimming()
    {
        using var fixture = new Fixture();
        var paths = Enumerable.Range(1, 5).Select(day => fixture.AddSnapshot(day)).ToArray();
        File.WriteAllText(Path.Combine(paths[2], "manifest.json"), "invalid");
        Directory.CreateDirectory(Path.Combine(fixture.Service.BackupDirectory, "protected"));
        Directory.CreateDirectory(Path.Combine(fixture.Service.BackupDirectory, "unrelated", Path.GetFileName(paths[0])));
        var before = Fingerprint(fixture.App);

        var result = fixture.Service.GetBeforeRestoreBackups();

        Assert.IsNull(result.Error);
        CollectionAssert.AreEqual(paths.Reverse().Select(Path.GetFileName).ToArray(),
            result.Backups.Select(backup => backup.DirectoryName).ToArray());
        Assert.AreEqual(4, result.Backups.Count(backup => backup.Preview.IsValid));
        Assert.AreEqual(Guid.Empty, result.Backups[2].Preview.BackupId);
        Assert.IsNull(result.Backups[2].Preview.CreatedUtc);
        var row = new BeforeRestoreBackupRow(result.Backups[2]);
        StringAssert.Contains(row.Label, Path.GetFileName(paths[2]));
        var valid = result.Backups[0];
        Assert.AreEqual(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc), valid.Preview.CreatedUtc);
        Assert.HasCount(1, valid.Preview.Files);
        Assert.AreNotEqual(Guid.Empty, valid.Preview.BackupId);
        StringAssert.Contains(new BeforeRestoreBackupRow(valid).Label, valid.Preview.BackupId.ToString("N")[..8]);
        CollectionAssert.AreEqual(before, Fingerprint(fixture.App));
    }

    [TestMethod]
    public void UnreadableSnapshotDoesNotBlockItsNeighbor()
    {
        using var fixture = new Fixture();
        var path = fixture.AddSnapshot(1);
        fixture.AddSnapshot(2);
        using var locked = new FileStream(Path.Combine(path, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = fixture.Service.GetBeforeRestoreBackups();
        Assert.HasCount(2, result.Backups);
        Assert.IsTrue(result.Backups[0].Preview.IsValid);
        Assert.IsFalse(result.Backups[1].Preview.IsValid);
    }

    [TestMethod]
    public void MalformedSnapshotRootDoesNotBlockOtherSnapshots()
    {
        using var fixture = new Fixture();
        var path = fixture.AddSnapshot(1);
        fixture.AddSnapshot(2);
        var manifestPath = Path.Combine(path, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FullBackupManifest>(File.ReadAllBytes(manifestPath), BackupFormat.JsonOptions)!;
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { NotesRoot = null! }, BackupFormat.JsonOptions));
        var result = fixture.Service.GetBeforeRestoreBackups();
        Assert.IsNull(result.Error);
        Assert.HasCount(2, result.Backups);
        Assert.IsTrue(result.Backups[0].Preview.IsValid);
        Assert.IsFalse(result.Backups[1].Preview.IsValid);
    }

    [TestMethod]
    [DataRow("deleted")]
    [DataRow("payload")]
    [DataRow("id")]
    [DataRow("type")]
    [DataRow("root")]
    [DataRow("metadata")]
    [DataRow("entries")]
    public void StaleSelectionCannotReadOrExport(string change)
    {
        using var fixture = new Fixture();
        var path = fixture.AddSnapshot(1);
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var file = backup.Preview.Files.Single();
        var manifestPath = Path.Combine(path, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FullBackupManifest>(File.ReadAllBytes(manifestPath), BackupFormat.JsonOptions)!;
        switch (change)
        {
            case "deleted": Directory.Delete(path, true); break;
            case "payload": File.WriteAllText(Path.Combine(path, "files", "notes", "one.txt"), "altered"); break;
            default:
                manifest = change switch
                {
                    "id" => manifest with { BackupId = Guid.NewGuid() },
                    "type" => manifest with { Type = FullBackupType.User },
                    "root" => manifest with { NotesRoot = fixture.CurrentNotes },
                    "metadata" => manifest with { Entries = manifest.Entries.Select(entry => entry with { ContentLength = 99 }).ToList() },
                    _ => manifest with { Entries = [null!] }
                };
                File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.JsonOptions));
                break;
        }

        var content = fixture.Service.ReadBeforeRestoreBackupFile(backup, file);
        Assert.IsFalse(content.Success);
        Assert.IsTrue(content.NeedsRecheck);
        var exported = fixture.Service.ExportBeforeRestoreBackupFile(backup, file, fixture.ExportPath);
        Assert.IsFalse(exported.Success);
        Assert.IsFalse(File.Exists(fixture.ExportPath));
        Assert.HasCount(0, Directory.GetFiles(fixture.Output, ".noted-export-*"));
    }

    [TestMethod]
    public void ComparisonsUseHistoricalNotesRoot()
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        File.WriteAllBytes(Path.Combine(fixture.OldNotes, "one.txt"), Fixture.Content);
        File.WriteAllText(Path.Combine(fixture.CurrentNotes, "one.txt"), "different current folder");
        var file = fixture.Service.GetBeforeRestoreBackups().Backups.Single().Preview.Files.Single();
        Assert.AreEqual(BackupFileComparison.Unchanged, file.Comparison);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("large")]
    [DataRow("binary")]
    [DataRow("json")]
    [DataRow("rtf")]
    public void ExportCopiesStoredBytesEvenWhenPreviewCannotDisplayThem(string kind)
    {
        using var fixture = new Fixture();
        var data = kind switch
        {
            "large" => Enumerable.Repeat((byte)'x', 4 * 1024 * 1024 + 1).ToArray(),
            "binary" => new byte[] { 0xff, 0xfe, 0, 1, 2 },
            "json" => Encoding.UTF8.GetBytes("{\"answer\": 42}"),
            "rtf" => Encoding.UTF8.GetBytes("{\\rtf1 stored}"),
            _ => Fixture.Content
        };
        var extension = kind is "json" or "rtf" ? kind : kind == "binary" ? "bin" : "txt";
        fixture.AddSnapshot(1, data, $"notes/one.{extension}");
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var file = backup.Preview.Files.Single();
        var before = Fingerprint(fixture.App);
        var preview = fixture.Service.ReadBeforeRestoreBackupFile(backup, file);
        Assert.AreEqual(kind != "large", preview.Success);
        Assert.IsFalse(preview.NeedsRecheck);
        if (kind == "large")
            StringAssert.Contains(preview.Error!, "too large");

        var exported = fixture.Service.ExportBeforeRestoreBackupFile(backup, file, fixture.ExportPath);

        Assert.IsTrue(exported.Success, exported.Message);
        CollectionAssert.AreEqual(data, File.ReadAllBytes(fixture.ExportPath));
        CollectionAssert.AreEqual(before, Fingerprint(fixture.App));
        Assert.HasCount(0, Directory.GetFiles(fixture.Output, ".noted-export-*"));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Service.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    [DataRow("app")]
    [DataRow("backup")]
    [DataRow("current")]
    [DataRow("historical")]
    public void ProtectedDirectoriesAndTheirAliasesRejectExport(string target)
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var root = target switch
        {
            "app" => fixture.App,
            "backup" => fixture.Service.BackupDirectory,
            "current" => fixture.CurrentNotes,
            _ => fixture.OldNotes
        };
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        using var alias = new TestFileSystemLink(fixture.Directory.File("alias"), nested);
        foreach (var directory in new[] { root, nested, fixture.Directory.File("alias") })
        {
            var destination = Path.Combine(directory, "export.txt");
            var result = fixture.Service.ExportBeforeRestoreBackupFile(backup, backup.Preview.Files.Single(), destination);
            Assert.IsFalse(result.Success);
            Assert.IsFalse(File.Exists(destination));
        }
    }

    [TestMethod]
    public void ExplicitAliasToExternalDestinationIsAllowed()
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        using var alias = new TestFileSystemLink(fixture.Directory.File("alias"), fixture.Output);
        var result = fixture.Service.ExportBeforeRestoreBackupFile(backup, backup.Preview.Files.Single(),
            fixture.Directory.File("alias", "copy.txt"));
        Assert.IsTrue(result.Success, result.Message);
        CollectionAssert.AreEqual(Fixture.Content, File.ReadAllBytes(Path.Combine(fixture.Output, "copy.txt")));
    }

    [TestMethod]
    public void ForgedSnapshotPathsOrFileMetadataAreRejected()
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var file = backup.Preview.Files.Single();
        foreach (var name in new[] { "../" + backup.DirectoryName, "protected", Path.Combine("before-restore-fake", backup.DirectoryName) })
        {
            var forged = backup with { DirectoryName = name };
            Assert.IsFalse(fixture.Service.ReadBeforeRestoreBackupFile(forged, file).Success);
            Assert.IsFalse(fixture.Service.ExportBeforeRestoreBackupFile(forged, file, fixture.ExportPath).Success);
        }
        Assert.IsFalse(fixture.Service.ExportBeforeRestoreBackupFile(backup, file with { Length = file.Length + 1 }, fixture.ExportPath).Success);
        Assert.IsFalse(File.Exists(fixture.ExportPath));
    }

    [TestMethod]
    public void LinkedExistingDestinationCannotModifyItsTarget()
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var target = Path.Combine(fixture.CurrentNotes, "live.txt");
        File.WriteAllText(target, "keep live data");
        using var link = new TestFileSystemLink(fixture.ExportPath, target, isDirectory: false);
        Assert.IsFalse(fixture.Service.ExportBeforeRestoreBackupFile(backup, backup.Preview.Files.Single(), fixture.ExportPath).Success);
        Assert.AreEqual("keep live data", File.ReadAllText(target));
    }

    [TestMethod]
    [DataRow("manifest")]
    [DataRow("payload")]
    [DataRow("snapshot")]
    [DataRow("storage")]
    public void LinkedSnapshotContentCannotBeBrowsedOrExported(string target)
    {
        using var fixture = new Fixture();
        var path = fixture.AddSnapshot(1);
        var selected = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var linkPath = target switch
        {
            "manifest" => Path.Combine(path, "manifest.json"),
            "payload" => Path.Combine(path, "files", "notes", "one.txt"),
            "snapshot" => path,
            _ => fixture.Service.BackupDirectory
        };
        var outside = fixture.Directory.File("outside");
        var isDirectory = target is "snapshot" or "storage";
        if (isDirectory) Directory.Move(linkPath, outside);
        else File.Move(linkPath, outside);
        using var link = new TestFileSystemLink(linkPath, outside, isDirectory);
        var before = isDirectory ? Fingerprint(outside) : [Convert.ToHexString(File.ReadAllBytes(outside))];
        var result = fixture.Service.GetBeforeRestoreBackups();
        Assert.IsTrue(result.Error is not null || result.Backups.All(backup => !backup.Preview.IsValid));
        Assert.IsTrue(fixture.Service.ReadBeforeRestoreBackupFile(selected, selected.Preview.Files.Single()).NeedsRecheck);
        Assert.IsFalse(fixture.Service.ExportBeforeRestoreBackupFile(selected, selected.Preview.Files.Single(), fixture.ExportPath).Success);
        CollectionAssert.AreEqual(before, isDirectory ? Fingerprint(outside) : [Convert.ToHexString(File.ReadAllBytes(outside))]);
    }

    [TestMethod]
    [DataRow("existing")]
    [DataRow("missing-directory")]
    [DataRow("directory")]
    public void InvalidDestinationsPreserveExistingFilesAndLeaveNoTemporaryFile(string kind)
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        var backup = fixture.Service.GetBeforeRestoreBackups().Backups.Single();
        var destination = fixture.ExportPath;
        if (kind == "existing") File.WriteAllText(destination, "keep me");
        if (kind == "directory") Directory.CreateDirectory(destination);
        if (kind == "missing-directory") destination = Path.Combine(fixture.Output, "missing", "copy.txt");
        var before = Fingerprint(fixture.Output);
        Assert.IsFalse(fixture.Service.ExportBeforeRestoreBackupFile(backup, backup.Preview.Files.Single(), destination).Success);
        CollectionAssert.AreEqual(before, Fingerprint(fixture.Output));
        Assert.HasCount(0, Directory.GetFiles(fixture.Output, ".noted-export-*"));
    }

    [TestMethod]
    public void PublicationDoesNotOverwriteADestinationCreatedAfterVerification()
    {
        using var directory = new TemporaryTestDirectory();
        var temp = directory.File("temporary");
        var destination = directory.File("export.txt");
        File.WriteAllBytes(temp, Fixture.Content);
        var entry = Entry(Fixture.Content, "notes/one.txt", "unused");
        Assert.ThrowsExactly<IOException>(() => FullBackupService.PublishNewBackupFile(temp, destination, entry,
            () => File.WriteAllText(destination, "created during export")));
        Assert.AreEqual("created during export", File.ReadAllText(destination));
        Assert.IsNull(FullBackupService.CleanupFileExport(temp));
        Assert.IsFalse(File.Exists(temp));
    }

    [TestMethod]
    public void VerificationOrFinalSourceCheckFailureDoesNotPublish()
    {
        using var directory = new TemporaryTestDirectory();
        var temp = directory.File("temporary");
        var destination = directory.File("export.txt");
        var entry = Entry(Fixture.Content, "notes/one.txt", "unused");
        File.WriteAllText(temp, "incorrect bytes");
        Assert.ThrowsExactly<InvalidDataException>(() => FullBackupService.PublishNewBackupFile(temp, destination, entry, () => { }));
        File.WriteAllBytes(temp, Fixture.Content);
        Assert.ThrowsExactly<IOException>(() => FullBackupService.PublishNewBackupFile(temp, destination, entry,
            () => throw new IOException("source is no longer available")));
        Assert.IsFalse(File.Exists(destination));
        Assert.IsNull(FullBackupService.CleanupFileExport(temp));
    }

    [TestMethod]
    public void PublicationWriteFailureLeavesOnlyTheTemporaryFileForCleanup()
    {
        using var directory = new TemporaryTestDirectory();
        var temp = directory.File("temporary");
        var destination = directory.File("export.txt");
        File.WriteAllBytes(temp, Fixture.Content);
        Assert.ThrowsExactly<IOException>(() => FullBackupService.PublishNewBackupFile(temp, destination,
            Entry(Fixture.Content, "notes/one.txt", "unused"), () => Directory.CreateDirectory(destination)));
        Assert.IsTrue(Directory.Exists(destination));
        CollectionAssert.AreEqual(Fixture.Content, File.ReadAllBytes(temp));
        Assert.IsNull(FullBackupService.CleanupFileExport(temp));
        Assert.IsFalse(File.Exists(temp));
    }

    [TestMethod]
    public void TemporaryFileChangedDuringFinalChecksIsNotPublished()
    {
        using var directory = new TemporaryTestDirectory();
        var temp = directory.File("temporary");
        var destination = directory.File("export.txt");
        File.WriteAllBytes(temp, Fixture.Content);
        Assert.ThrowsExactly<InvalidDataException>(() => FullBackupService.PublishNewBackupFile(temp, destination,
            Entry(Fixture.Content, "notes/one.txt", "unused"), () => File.WriteAllText(temp, "changed")));
        Assert.IsFalse(File.Exists(destination));
        Assert.IsNull(FullBackupService.CleanupFileExport(temp));
    }

    [TestMethod]
    public void CleanupFailureReportsTheLeftoverFileAndCanBeRetried()
    {
        using var directory = new TemporaryTestDirectory();
        var temp = directory.File("temporary");
        File.WriteAllBytes(temp, Fixture.Content);
        using (var locked = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var warning = FullBackupService.CleanupFileExport(temp);
            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, temp);
            Assert.IsTrue(File.Exists(temp));
        }
        Assert.IsNull(FullBackupService.CleanupFileExport(temp));
        Assert.IsFalse(File.Exists(temp));
    }

    [TestMethod]
    public void CoordinatorUsesCurrentNotesRootWithoutFlushingOrRequestingRestore()
    {
        using var fixture = new Fixture();
        fixture.AddSnapshot(1);
        var changedNotes = fixture.Directory.File("changed-notes");
        Directory.CreateDirectory(changedNotes);
        var flush = new ShutdownFlushCoordinator();
        var coordinator = new BackupCoordinator(fixture.Service, flush, () => changedNotes);
        using var registration = coordinator.RegisterPreparationParticipant("must not flush", BackupPreparationPhase.SharedFlush,
            () => throw new AssertFailedException("Browsing must not flush application data."), () => null);
        coordinator.ShutdownRequested += (_, _) => Assert.Fail("Browsing must not request shutdown.");
        var before = Fingerprint(fixture.App);
        var backup = coordinator.GetBeforeRestoreBackups().Backups.Single();
        Assert.IsTrue(coordinator.ReadBeforeRestoreBackupFile(backup, backup.Preview.Files.Single()).Success);
        Assert.IsFalse(coordinator.ExportBeforeRestoreBackupFile(backup, backup.Preview.Files.Single(),
            Path.Combine(changedNotes, "copy.txt")).Success);
        Assert.IsTrue(coordinator.ExportBeforeRestoreBackupFile(backup, backup.Preview.Files.Single(), fixture.ExportPath).Success);
        Assert.IsFalse(coordinator.HasRequestedRestore);
        CollectionAssert.AreEqual(before, Fingerprint(fixture.App));
    }

    private static string[] Fingerprint(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    private static FullBackupEntry Entry(byte[] data, string logicalPath, string original) =>
        new(logicalPath, original, data.LongLength, Convert.ToHexString(SHA256.HashData(data)), null, data.LongLength);

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] Content = Encoding.UTF8.GetBytes("original\r\nbytes\n");
        internal TemporaryTestDirectory Directory { get; } = new();
        internal string App => Directory.File("app");
        internal string CurrentNotes => Directory.File("current");
        internal string OldNotes => Directory.File("historical");
        internal string Output => Directory.File("output");
        internal string ExportPath => Path.Combine(Output, "copy.txt");
        internal FullBackupService Service { get; }

        internal Fixture()
        {
            foreach (var path in new[] { App, CurrentNotes, OldNotes, Output })
                System.IO.Directory.CreateDirectory(path);
            Service = new FullBackupService(App, CurrentNotes);
        }

        internal string AddSnapshot(int day, byte[]? bytes = null, string logicalPath = "notes/one.txt")
        {
            bytes ??= Content;
            var date = new DateTime(2026, 1, day, 12, 0, 0, DateTimeKind.Utc);
            var path = Path.Combine(Service.BackupDirectory, $"before-restore-{date:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            var payload = Path.Combine(path, "files", logicalPath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            File.WriteAllBytes(payload, bytes);
            var manifest = new FullBackupManifest(1, Guid.NewGuid(), FullBackupType.BeforeRestore, date, App, OldNotes,
                [Entry(bytes, logicalPath, Path.Combine(OldNotes, Path.GetFileName(payload)))]);
            File.WriteAllBytes(Path.Combine(path, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.JsonOptions));
            return path;
        }

        public void Dispose() => Directory.Dispose();
    }
}
