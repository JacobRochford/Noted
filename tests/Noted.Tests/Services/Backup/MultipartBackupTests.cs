using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class MultipartBackupTests
{
    [TestMethod]
    [DataRow("one.txt", false)]
    [DataRow("nested/hidden.bin", true)]
    public void ExportAutomaticallyChoosesFormatAndRestoresExactBytes(string relative, bool multipart)
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("app");
        var notes = directory.File("notes");
        var note = Path.Combine(notes, relative);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        var bytes = Enumerable.Range(0, 4097).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(note, bytes);
        File.SetAttributes(note, FileAttributes.Hidden);
        var service = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup(true,
            _ => throw new AssertFailedException("A portable backup must not show a local-only warning.")).Status);
        Assert.IsNull(service.GetUserBackupInfo().ExportIneligibility);
        var export = directory.File("portable.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(export).Success);
        using (var input = File.OpenRead(export))
            Assert.AreEqual(multipart, MultipartBackupContainer.IsMultipart(input));

        var targetApp = directory.File("target-app");
        var targetNotes = directory.File("target-notes");
        Directory.CreateDirectory(targetApp);
        Directory.CreateDirectory(targetNotes);
        var target = Path.Combine(targetNotes, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "protect this current file");
        File.WriteAllText(Path.Combine(targetNotes, "unrelated.txt"), "keep me");
        var importer = new FullBackupService(targetApp, targetNotes);
        var preview = importer.GetBackupImportPreview(export);
        Assert.IsTrue(preview.IsValid, preview.Error);
        Assert.IsTrue(importer.ReadBackupImportFile(export, preview.BackupId, preview.VerificationToken!, preview.Files.Single()).Success);
        var prepared = importer.PrepareBackupRestore(export, preview.BackupId, preview.VerificationToken);
        Assert.IsTrue(prepared.Success, prepared.ProtectionResult.Message);
        var protection = importer.GetBeforeRestoreBackups().Backups.Single();
        Assert.IsTrue(protection.Preview.Files.Any(file => file.LogicalPath == "notes/" + relative));
        Assert.AreEqual(FullBackupStatus.Restored, importer.ApplyPendingUserBackupRestore().Status);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(targetNotes, "unrelated.txt")));
    }

    [TestMethod]
    public void IndependentFramedFixtureReassemblesMultipleParts()
    {
        using var directory = new TemporaryTestDirectory();
        var bytes = Enumerable.Range(0, 3000).Select(index => (byte)index).ToArray();
        var path = WriteFixture(directory, bytes);
        using var output = new MemoryStream();
        var archive = MultipartBackupContainer.Read(path, (_, _, stream) => stream.CopyTo(output));
        Assert.IsTrue(archive.IsMultipart);
        CollectionAssert.AreEqual(bytes, output.ToArray());
        Assert.AreEqual(3000L, archive.Manifest.Entries.Single().Length);
    }

    [TestMethod]
    public void MultipartReusesSettingsAndEditorSessionRebasing()
    {
        using var directory = new TemporaryTestDirectory();
        var app = directory.File("source-app");
        var notes = directory.File("source-notes");
        Directory.CreateDirectory(notes);
        new AppSettingsService(app).SaveNotesDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "one.txt"), "saved content");
        File.WriteAllBytes(Path.Combine(notes, "opaque.bin"), [0, 255]);
        BackupTestSession.Save(app, Path.Combine(notes, "one.txt"));
        var service = new FullBackupService(app, notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup(true).Status);
        var export = directory.File("rebased.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(export).Success);
        var targetApp = directory.File("target-app");
        var targetNotes = directory.File("target-notes");
        Directory.CreateDirectory(targetApp);
        Directory.CreateDirectory(targetNotes);
        var importer = new FullBackupService(targetApp, targetNotes);
        var preview = importer.GetBackupImportPreview(export);
        Assert.IsTrue(preview.IsValid);
        Assert.IsTrue(importer.PrepareBackupRestore(export, preview.BackupId, preview.VerificationToken).Success);
        Assert.AreEqual(FullBackupStatus.Restored, importer.ApplyPendingUserBackupRestore().Status);
        Assert.AreEqual(targetNotes, new AppSettingsService(targetApp).LoadNotesDirectory());
        var session = JsonSerializer.Deserialize<NoteEditorSession>(File.ReadAllBytes(
            Path.Combine(targetApp, "session", "editor-workspace.json")))!;
        Assert.AreEqual(Path.Combine(targetNotes, "one.txt"), session.Tabs.Single().FilePath);
        CollectionAssert.AreEqual(new byte[] { 0, 255 }, File.ReadAllBytes(Path.Combine(targetNotes, "opaque.bin")));
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("reordered")]
    [DataRow("corrupt")]
    [DataRow("wrong-file-hash")]
    [DataRow("wrong-set")]
    [DataRow("unexpected")]
    [DataRow("huge-record")]
    public void InvalidCompleteSetNeverSchedulesRestore(string problem)
    {
        using var directory = new TemporaryTestDirectory();
        var path = WriteFixture(directory, new byte[3000]);
        var bytes = File.ReadAllBytes(path);
        using (var memory = new MemoryStream(bytes))
        using (var reader = new BinaryReader(memory))
        {
            memory.Position = 8;
            var headerLength = reader.ReadInt32();
            memory.Position += headerLength + 4;
            var fileLength = reader.ReadInt32();
            var fileStart = (int)memory.Position;
            var partStart = fileStart + fileLength;
            switch (problem)
            {
                case "truncated": Array.Resize(ref bytes, bytes.Length - 1); break;
                case "missing": bytes = bytes[..partStart].Concat(bytes[(partStart + 1068)..]).ToArray(); break;
                case "duplicate": BitConverter.GetBytes(0L).CopyTo(bytes, partStart + 1068); break;
                case "reordered": BitConverter.GetBytes(1L).CopyTo(bytes, partStart); break;
                case "corrupt": bytes[partStart + 44] ^= 1; break;
                case "wrong-file-hash":
                    var text = Encoding.UTF8.GetString(bytes, fileStart, fileLength);
                    var hash = Convert.ToHexString(SHA256.HashData(new byte[3000]));
                    var changed = text.Replace(hash, new string('0', 64));
                    Encoding.UTF8.GetBytes(changed).CopyTo(bytes, fileStart);
                    break;
                case "wrong-set": bytes[^1] ^= 1; break;
                case "unexpected": bytes = bytes.Concat(new byte[] { 1 }).ToArray(); break;
                case "huge-record": BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 8); break;
            }
        }
        File.WriteAllBytes(path, bytes);
        var service = new FullBackupService(directory.File("target-app"), directory.File("target-notes"));
        Assert.IsFalse(service.GetBackupImportPreview(path).IsValid);
        Assert.AreEqual(FullBackupStatus.Failed, service.ScheduleBackupImportRestore(path, Guid.NewGuid(), "token").Status);
        Assert.IsFalse(Directory.Exists(directory.File("target-notes")));
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

    [TestMethod]
    [DataRow("notes/../escape.bin")]
    [DataRow("notes/CON.txt")]
    [DataRow("notes/file.txt:stream")]
    [DataRow("app/settings.json")]
    [DataRow("app/unknown.bin")]
    public void MultipartDoesNotBypassPathsOrManagedValidation(string logicalPath)
    {
        using var directory = new TemporaryTestDirectory();
        var path = WriteFixture(directory, "{invalid"u8.ToArray(), logicalPath);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.IsFalse(service.GetBackupImportPreview(path).IsValid);
    }

    [TestMethod]
    public void LargeCountsAndMetadataAreReadWithoutLegacyLimits()
    {
        using var directory = new TemporaryTestDirectory();
        var entries = Enumerable.Range(0, 10_001).Select(index =>
            Entry($"notes/{index}.bin", []) with { OriginalPath = @"C:\old\" + new string('x', 500) }).ToList();
        var manifest = Manifest(directory, entries);
        var path = directory.File("many.notedbackup");
        using (var output = File.Create(path))
            MultipartBackupContainer.Write(output, manifest, _ => new MemoryStream());
        Assert.IsGreaterThan(4L * 1024 * 1024, new FileInfo(path).Length);
        var read = MultipartBackupContainer.Read(path);
        Assert.AreEqual(10_001, read.Manifest.Entries.Count);
        Assert.IsTrue(new FullBackupService(directory.File("app"), directory.File("notes")).GetBackupImportPreview(path).IsValid);
    }

    [TestMethod]
    [DataRow(1L << 40, true)]
    [DataRow((1L << 40) + 1, false)]
    public void IndividualCapacityUses64BitLengthsWithoutAllocatingPayload(long length, bool valid)
    {
        using var directory = new TemporaryTestDirectory();
        var manifest = Manifest(directory, [Entry("notes/large.bin", []) with { Length = length }]);
        if (valid) MultipartBackupContainer.ValidateCapacity(manifest);
        else Assert.ThrowsExactly<InvalidDataException>(() => MultipartBackupContainer.ValidateCapacity(manifest));
    }

    [TestMethod]
    public void CombinedCapacityAndRecordSizeAreBounded()
    {
        using var directory = new TemporaryTestDirectory();
        var entries = Enumerable.Range(0, 16).Select(i => Entry($"notes/{i}.bin", []) with { Length = 1L << 40 }).ToList();
        MultipartBackupContainer.ValidateCapacity(Manifest(directory, entries));
        entries.Add(Entry("notes/overflow.bin", [1]));
        Assert.ThrowsExactly<InvalidDataException>(() => MultipartBackupContainer.ValidateCapacity(Manifest(directory, entries)));
        Assert.ThrowsExactly<InvalidDataException>(() => MultipartBackupContainer.ValidateCapacity(Manifest(directory,
            [Entry("notes/one.bin", []) with { OriginalPath = new string('x', 65_536) }])));
    }

    [TestMethod]
    public void ChangedSourceOrCancellationCannotProduceACompleteContainer()
    {
        using var directory = new TemporaryTestDirectory();
        var manifest = Manifest(directory, [Entry("notes/data.bin", new byte[3000])]);
        using var output = new MemoryStream();
        Assert.ThrowsExactly<InvalidDataException>(() => MultipartBackupContainer.Write(output, manifest,
            _ => new MemoryStream(Enumerable.Repeat((byte)1, 3000).ToArray()), partSize: 1024));
        var path = directory.File("incomplete.notedbackup");
        File.WriteAllBytes(path, output.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => MultipartBackupContainer.Read(path));
        using var cancellation = new CancellationTokenSource();
        using var cancelledOutput = new MemoryStream();
        Assert.ThrowsExactly<OperationCanceledException>(() => MultipartBackupContainer.Write(cancelledOutput, manifest,
            _ => { cancellation.Cancel(); return new MemoryStream(new byte[3000]); }, cancellation.Token, 1024));
        File.WriteAllText(path, "existing export");
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.IsFalse(service.ExportUserBackup(path, cancellation.Token).Success);
        Assert.AreEqual("existing export", File.ReadAllText(path));
        Assert.AreEqual(FullBackupStatus.Failed,
            service.ScheduleBackupImportRestore(path, manifest.BackupId, "token", cancellation.Token).Status);
    }

    [TestMethod]
    public void DuplicateRebasedDestinationsAndFileDirectoryCollisionsAreRejected()
    {
        using var directory = new TemporaryTestDirectory();
        foreach (var paths in new[] { new[] { "notes/same.bin", "notes/SAME.bin" }, new[] { "notes/file", "notes/file/child.bin" } })
        {
            var path = directory.File("collision.notedbackup");
            using (var output = File.Create(path))
                MultipartBackupContainer.Write(output, Manifest(directory, paths.Select(p => Entry(p, [])).ToList()), _ => new MemoryStream());
            Assert.IsFalse(new FullBackupService(directory.File("app"), directory.File("notes")).GetBackupImportPreview(path).IsValid);
        }
    }

    [TestMethod]
    public void CancellationAfterVerificationLeavesExistingExportUntouched()
    {
        using var directory = new TemporaryTestDirectory();
        var destination = directory.File("existing.notedbackup");
        var temporary = directory.File("export.tmp");
        File.WriteAllText(destination, "previous verified export");
        File.WriteAllText(temporary, "new export");
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => FullBackupService.PublishBackupExport(
            temporary, destination, _ => cancellation.Cancel(), cancellation.Token));
        Assert.AreEqual("previous verified export", File.ReadAllText(destination));
        Assert.IsTrue(File.Exists(temporary));
        Assert.IsNull(FullBackupService.CleanupFileExport(temporary));
    }

    [TestMethod]
    public void AliasedAppAndNotesDestinationsCannotOverwriteTheSameFile()
    {
        using var directory = new TemporaryTestDirectory();
        var targetApp = directory.File("app");
        Directory.CreateDirectory(targetApp);
        var alias = directory.File("notes-alias");
        using var link = new TestFileSystemLink(alias, targetApp);
        var entries = new[] { Entry("app/settings.json", "{}"u8.ToArray()), Entry("notes/settings.json", "{}"u8.ToArray()) };
        var path = directory.File("collision.notedbackup");
        using (var output = File.Create(path))
            MultipartBackupContainer.Write(output, Manifest(directory, entries), _ => new MemoryStream("{}"u8.ToArray()));
        Assert.IsFalse(new FullBackupService(targetApp, alias).GetBackupImportPreview(path).IsValid);
        Assert.IsFalse(File.Exists(Path.Combine(targetApp, "settings.json")));
    }

    [TestMethod]
    public void FileCountLimitIsCheckedBeforeReadingAnyPayload()
    {
        using var directory = new TemporaryTestDirectory();
        var entries = Enumerable.Repeat(Entry("notes/empty.bin", []), 100_001).ToList();
        Assert.ThrowsExactly<InvalidDataException>(() => MultipartBackupContainer.ValidateCapacity(Manifest(directory, entries)));
    }

    [TestMethod]
    public void ExplicitArchiveFolderAliasWorksButLinkedArchiveFileIsRejected()
    {
        using var directory = new TemporaryTestDirectory();
        using var aliases = new TemporaryTestDirectory();
        var path = WriteFixture(directory, [1, 2, 3]);
        var alias = aliases.File("folder");
        using var folderLink = new TestFileSystemLink(alias, directory.Path);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        Assert.IsTrue(service.GetBackupImportPreview(Path.Combine(alias, Path.GetFileName(path))).IsValid);
        var fileAlias = aliases.File("file.notedbackup");
        using var fileLink = new TestFileSystemLink(fileAlias, path, isDirectory: false);
        Assert.IsFalse(service.GetBackupImportPreview(fileAlias).IsValid);
    }

    [TestMethod]
    public void WriteFailureLeavesAnIncompleteContainerThatCannotBeImported()
    {
        using var directory = new TemporaryTestDirectory();
        var manifest = Manifest(directory, [Entry("notes/data.bin", new byte[4096])]);
        using var output = new FailingWriteStream();
        Assert.ThrowsExactly<IOException>(() => MultipartBackupContainer.Write(output, manifest,
            _ => new MemoryStream(new byte[4096]), partSize: 1024));
        var path = directory.File("partial.notedbackup");
        File.WriteAllBytes(path, output.ToArray());
        Assert.IsFalse(new FullBackupService(directory.File("app"), directory.File("notes")).GetBackupImportPreview(path).IsValid);
    }

    [TestMethod]
    public void ChangedArchiveIdentityAfterPreviewCannotScheduleRestore()
    {
        using var directory = new TemporaryTestDirectory();
        var path = WriteFixture(directory, [1, 2, 3]);
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        var preview = service.GetBackupImportPreview(path);
        Assert.IsTrue(preview.IsValid);
        WriteFixture(directory, [4, 5, 6]);
        Assert.AreEqual(FullBackupStatus.Blocked,
            service.ScheduleBackupImportRestore(path, preview.BackupId, preview.VerificationToken!).Status);
        Assert.IsFalse(File.Exists(Path.Combine(service.BackupDirectory, "restore-request.json")));
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > 2048) throw new IOException("Simulated disk-full write.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > 2048) throw new IOException("Simulated disk-full write.");
            base.Write(buffer);
        }
    }

    private static string WriteFixture(TemporaryTestDirectory directory, byte[] bytes, string logicalPath = "notes/opaque.bin")
    {
        var path = directory.File("fixture.notedbackup");
        var entry = Entry(logicalPath, bytes);
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 2, Backup = Manifest(directory, []), FileCount = 1, PartSize = 1024
        }, BackupFormat.JsonOptions);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(entry, BackupFormat.JsonOptions);
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write("NOTEDMP2"u8);
        writer.Write(header.Length);
        writer.Write(header);
        writer.Write(0);
        writer.Write(metadata.Length);
        writer.Write(metadata);
        for (var offset = 0; offset < bytes.Length; offset += 1024)
        {
            var part = bytes.AsSpan(offset, Math.Min(1024, bytes.Length - offset));
            writer.Write((long)(offset / 1024));
            writer.Write(part.Length);
            writer.Write(SHA256.HashData(part));
            writer.Write(part);
        }
        writer.Write("NOTEDEND"u8);
        writer.Write(SHA256.HashData(header.Concat(metadata).ToArray()));
        return path;
    }

    private static FullBackupEntry Entry(string path, byte[] bytes) =>
        new(path, @"C:\original\" + path, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), null, bytes.LongLength);

    private static FullBackupManifest Manifest(TemporaryTestDirectory directory, IReadOnlyList<FullBackupEntry> entries) =>
        new(1, Guid.NewGuid(), FullBackupType.User, DateTime.UtcNow, directory.File("source-app"), directory.File("source-notes"), entries);
}
