using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

public sealed partial class BackupArchiveReaderTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ImportRejectsExcessiveZipAndManifestFileCounts(bool zipEntries)
    {
        using var directory = new TemporaryTestDirectory();
        var archivePath = directory.File("count.notedbackup");
        var files = Enumerable.Range(0, 10_001).Select(index => ($"notes/{index}.txt", 0L)).ToArray();
        WriteEligibilityArchive(archivePath, directory, files, includePayloads: zipEntries);

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            CreateReader(directory.File("app"), directory.File("notes")).ReadBackupArchive(archivePath));

        StringAssert.Contains(error.Message, zipEntries ? "10002 ZIP entries" : "10001 files");
        StringAssert.Contains(error.Message, "at most 10000 files");
    }

    [TestMethod]
    [DataRow(0L, true)]
    [DataRow(1L, false)]
    public void ImportChecksTheNonJsonPayloadBoundaryFromZipMetadata(long extraBytes, bool eligible)
    {
        using var directory = new TemporaryTestDirectory();
        var archivePath = directory.File("payload.notedbackup");
        const long limit = 256L * 1024 * 1024;
        WriteEligibilityArchive(archivePath, directory, [("notes/large.txt", limit + extraBytes)]);
        var reader = CreateReader(directory.File("app"), directory.File("notes"));

        if (eligible)
        {
            var archive = reader.ReadBackupArchive(archivePath);
            Assert.AreEqual(limit, archive.Manifest.Entries.Single().Length);
        }
        else
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadBackupArchive(archivePath));
            StringAssert.Contains(error.Message, "notes/large.txt");
            StringAssert.Contains(error.Message, "import limit of 256 MiB");
        }
        Assert.IsLessThan(16_384L, new FileInfo(archivePath).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ImportChecksCombinedUncompressedPayloadSizeWithoutAllocatingPayloads(bool oversized)
    {
        using var directory = new TemporaryTestDirectory();
        var archivePath = directory.File("total.notedbackup");
        var files = Enumerable.Range(0, 8).Select(index => ($"notes/{index}.txt", 256L * 1024 * 1024)).ToList();
        if (oversized)
            files.Add(("notes/extra.txt", 1));
        WriteEligibilityArchive(archivePath, directory, files);
        var reader = CreateReader(directory.File("app"), directory.File("notes"));

        if (oversized)
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadBackupArchive(archivePath));
            StringAssert.Contains(error.Message, "combined uncompressed payload import limit of 2048 MiB");
        }
        else
        {
            var archive = reader.ReadBackupArchive(archivePath);
            Assert.AreEqual(2L * 1024 * 1024 * 1024, archive.Manifest.Entries.Sum(file => file.Length));
        }
        Assert.IsLessThan(16_384L, new FileInfo(archivePath).Length);
    }

    [TestMethod]
    public void ImportInspectionRejectsDuplicateTransformedLogicalPaths()
    {
        using var directory = new TemporaryTestDirectory();
        var archivePath = directory.File("collision.notedbackup");
        WriteEligibilityArchive(archivePath, directory, [("notes/one.txt", 0), ("notes/two.txt", 0)]);
        var reader = new BackupArchiveReader(
            (path, manifest) => BackupFormat.GetContainedPath(manifest.NotesRoot, path["notes/".Length..]),
            (entry, bytes, _) => new ImportedBackupFile(entry.LogicalPath, "notes/same.txt", bytes, null, null),
            (manifest, _, _) => manifest);
        var archive = reader.ReadBackupArchive(archivePath);

        var error = Assert.ThrowsExactly<InvalidDataException>(() => reader.InspectImportedFiles(archive));

        StringAssert.Contains(error.Message, "Multiple imported files map to 'notes/same.txt'");
    }

    private static void WriteEligibilityArchive(
        string archivePath,
        TemporaryTestDirectory directory,
        IReadOnlyList<(string Path, long Length)> files,
        bool includePayloads = true)
    {
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            if (includePayloads)
            {
                foreach (var file in files)
                    archive.CreateEntry("files/" + file.Path);
            }
            var manifest = new
            {
                SchemaVersion = 1, SnapshotId = Guid.NewGuid(), Role = "Protected", CreatedUtc = DateTime.UtcNow,
                AppDataRoot = directory.File("app"), NotesRoot = directory.File("notes"),
                Entries = files.Select(file => new
                {
                    LogicalPath = file.Path, OriginalPath = "", file.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())),
                    ItemCount = (int?)null, ContentLength = (long?)null
                })
            };
            using var stream = archive.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(stream, manifest);
        }
        if (!includePayloads)
            return;

        // these fixtures exercise metadata eligibility only; oversized payloads are never read.
        var lengths = files.ToDictionary(file => "files/" + file.Path, file => file.Length, StringComparer.Ordinal);
        var bytes = File.ReadAllBytes(archivePath);
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4)));
        while (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) == 0x02014b50)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32, 2));
            var name = Encoding.UTF8.GetString(bytes, offset + 46, nameLength);
            if (lengths.TryGetValue(name, out var length))
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), checked((uint)length));
            offset += 46 + nameLength + extraLength + commentLength;
        }
        File.WriteAllBytes(archivePath, bytes);
    }
}
