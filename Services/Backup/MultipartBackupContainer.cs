using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

// one framed container: header, ordered file records and hashed parts, then a complete-set digest.
internal static class MultipartBackupContainer
{
    internal const int MaximumFiles = 100_000;
    internal const int MaximumMetadataBytes = 64 * 1024 * 1024;
    internal const int MaximumRecordBytes = 64 * 1024;
    internal const int DefaultPartSize = 8 * 1024 * 1024;
    internal const long MaximumFileBytes = 1L << 40;
    internal const long MaximumPayloadBytes = 16L << 40;
    private static readonly byte[] s_magic = "NOTEDMP2"u8.ToArray();
    private static readonly byte[] s_end = "NOTEDEND"u8.ToArray();

    internal static bool IsMultipart(Stream stream)
    {
        var position = stream.Position;
        if (stream.Length - position < 8) return false;
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        stream.Position = position;
        return magic.SequenceEqual(s_magic);
    }

    internal static void ValidateCapacity(FullBackupManifest manifest)
    {
        if (manifest.Entries.Count is < 1 or > MaximumFiles)
            throw new InvalidDataException($"Multipart backups support 1 to {MaximumFiles} files.");
        long total = 0;
        var metadata = Serialize(new Header(2, manifest with { Entries = [] }, manifest.Entries.Count, DefaultPartSize)).Length;
        foreach (var entry in manifest.Entries)
        {
            if (entry.Length < 0 || entry.Length > MaximumFileBytes)
                throw new InvalidDataException($"'{entry.LogicalPath}' exceeds the 1 TiB multipart file limit.");
            total += entry.Length;
            if (total > MaximumPayloadBytes)
                throw new InvalidDataException("The backup exceeds the 16 TiB multipart payload limit.");
            metadata = checked(metadata + Serialize(entry).Length);
            if (metadata > MaximumMetadataBytes)
                throw new InvalidDataException("The backup exceeds the 64 MiB multipart metadata limit.");
        }
    }

    internal static void Write(Stream destination, FullBackupManifest manifest,
        Func<FullBackupEntry, Stream> openSource, CancellationToken cancellationToken = default,
        int partSize = DefaultPartSize)
    {
        ValidateCapacity(manifest);
        ValidatePartSize(partSize);
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        using var metadataHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        writer.Write(s_magic);
        WriteRecord(writer, new Header(2, manifest with { Entries = [] }, manifest.Entries.Count, partSize), metadataHash);
        var buffer = new byte[partSize];
        for (var index = 0; index < manifest.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Entries[index];
            writer.Write(index);
            WriteRecord(writer, entry, metadataHash);
            using var source = openSource(entry);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = entry.Length;
            for (long part = 0; remaining > 0; part++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(partSize, remaining);
                source.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                writer.Write(part);
                writer.Write(count);
                writer.Write(SHA256.HashData(buffer.AsSpan(0, count)));
                writer.Write(buffer, 0, count);
                remaining -= count;
            }
            if (source.ReadByte() != -1 || !HashMatches(hash.GetHashAndReset(), entry.Sha256))
                throw new InvalidDataException($"'{entry.LogicalPath}' changed during export.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        writer.Write(s_end);
        writer.Write(metadataHash.GetHashAndReset());
        writer.Flush();
    }

    internal static BackupArchive Read(string path, Action<FullBackupManifest, FullBackupEntry, Stream>? consume = null,
        CancellationToken cancellationToken = default)
    {
        FileSystemLinkValidator.ValidatePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var metadataHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (!ReadBytes(reader, 8).AsSpan().SequenceEqual(s_magic))
            throw new InvalidDataException("The multipart backup signature is invalid.");
        var metadataBytes = 0;
        var header = ReadRecord<Header>(reader, metadataHash, ref metadataBytes);
        if (header.Version != 2 || header.Backup is null || header.Backup.Entries is null || header.Backup.Entries.Count != 0 ||
            header.FileCount is < 1 or > MaximumFiles)
            throw new InvalidDataException("The multipart backup header is invalid or unsupported.");
        ValidatePartSize(header.PartSize);
        var entries = new List<FullBackupEntry>();
        var buffer = new byte[header.PartSize];
        long total = 0;
        for (var index = 0; index < header.FileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ReadInt32() != index)
                throw new InvalidDataException("A multipart file record is missing, duplicated, or out of order.");
            var entry = ReadRecord<FullBackupEntry>(reader, metadataHash, ref metadataBytes);
            if (entry.Length < 0 || entry.Length > MaximumFileBytes || entry.Sha256 is null ||
                entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("A multipart file has invalid size or hash metadata.");
            total += entry.Length;
            if (total > MaximumPayloadBytes)
                throw new InvalidDataException("The multipart backup exceeds the 16 TiB payload limit.");
            using var file = new PartStream(reader, entry, buffer, cancellationToken);
            consume?.Invoke(header.Backup, entry, file);
            file.CopyTo(Stream.Null);
            file.Verify();
            entries.Add(entry);
        }
        var digest = metadataHash.GetHashAndReset();
        if (!ReadBytes(reader, 8).AsSpan().SequenceEqual(s_end) ||
            !ReadBytes(reader, 32).AsSpan().SequenceEqual(digest) || stream.ReadByte() != -1)
            throw new InvalidDataException("The multipart backup is incomplete, mismatched, or contains unexpected data.");
        return new BackupArchive(Path.GetFullPath(path), header.Backup with { Entries = entries }, Convert.ToHexString(digest), true);
    }

    private static byte[] Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BackupFormat.JsonOptions);
        if (bytes.Length > MaximumRecordBytes)
            throw new InvalidDataException("A multipart metadata record exceeds 64 KiB.");
        return bytes;
    }

    private static void WriteRecord<T>(BinaryWriter writer, T value, IncrementalHash hash)
    {
        var bytes = Serialize(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        hash.AppendData(bytes);
    }

    private static T ReadRecord<T>(BinaryReader reader, IncrementalHash hash, ref int total)
    {
        var length = reader.ReadInt32();
        if (length is < 1 or > MaximumRecordBytes || total > MaximumMetadataBytes - length)
            throw new InvalidDataException("Multipart metadata exceeds the permitted size.");
        total += length;
        var bytes = ReadBytes(reader, length);
        hash.AppendData(bytes);
        return JsonSerializer.Deserialize<T>(bytes, BackupFormat.JsonOptions)
            ?? throw new InvalidDataException("A multipart metadata record is empty.");
    }

    private static byte[] ReadBytes(BinaryReader reader, int count)
    {
        var bytes = new byte[count];
        reader.BaseStream.ReadExactly(bytes);
        return bytes;
    }

    private static bool HashMatches(byte[] hash, string expected) =>
        Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void ValidatePartSize(int size)
    {
        if (size < 1024 || size > DefaultPartSize || (size & (size - 1)) != 0)
            throw new InvalidDataException("The multipart part size is invalid.");
    }

    private sealed record Header(int Version, FullBackupManifest Backup, int FileCount, int PartSize);

    private sealed class PartStream(BinaryReader reader, FullBackupEntry entry, byte[] buffer, CancellationToken cancellationToken) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _available;
        private int _offset;
        private long _loaded;
        private long _part;
        private long _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override long Length => entry.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destination.Length == 0 || Position == Length) return 0;
            if (_available == 0)
            {
                var expected = (int)Math.Min(buffer.Length, Length - _loaded);
                if (reader.ReadInt64() != _part++ || reader.ReadInt32() != expected)
                    throw new InvalidDataException("A backup part is missing, duplicated, or out of order.");
                var hash = ReadBytes(reader, 32);
                reader.BaseStream.ReadExactly(buffer.AsSpan(0, expected));
                if (!SHA256.HashData(buffer.AsSpan(0, expected)).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException($"A part of '{entry.LogicalPath}' is corrupt.");
                _hash.AppendData(buffer, 0, expected);
                _loaded += expected;
                _available = expected;
                _offset = 0;
            }
            var count = Math.Min(destination.Length, _available);
            buffer.AsSpan(_offset, count).CopyTo(destination);
            _offset += count;
            _available -= count;
            _position += count;
            return count;
        }

        internal void Verify()
        {
            if (Position != Length || !HashMatches(_hash.GetHashAndReset(), entry.Sha256))
                throw new InvalidDataException($"The reassembled file '{entry.LogicalPath}' failed verification.");
        }

        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
