using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static Noted.Services.BackupContentCatalog;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed class BackupArchiveReader
{
    private const int MaximumImportFileCount = 10_000;
    private const long MaximumImportFileSize = 256L * 1024 * 1024;
    private const long MaximumImportJsonFileSize = 16L * 1024 * 1024;
    private const long MaximumImportTotalSize = 2L * 1024 * 1024 * 1024;
    private const long MaximumImportManifestSize = 4L * 1024 * 1024;
    private const long MaximumPreviewFileSize = 4 * 1024 * 1024;

    private readonly Func<string, FullBackupManifest, string> _getRestoreDestination;
    private readonly Func<FullBackupEntry, byte[], FullBackupManifest, ImportedBackupFile> _transformImportedFile;
    private readonly Func<FullBackupManifest, Guid, IReadOnlyList<ImportedBackupFileInfo>, FullBackupManifest> _createImportedManifest;

    internal BackupArchiveReader(
        Func<string, FullBackupManifest, string> getRestoreDestination,
        Func<FullBackupEntry, byte[], FullBackupManifest, ImportedBackupFile> transformImportedFile,
        Func<FullBackupManifest, Guid, IReadOnlyList<ImportedBackupFileInfo>, FullBackupManifest> createImportedManifest)
    {
        ArgumentNullException.ThrowIfNull(getRestoreDestination);
        ArgumentNullException.ThrowIfNull(transformImportedFile);
        ArgumentNullException.ThrowIfNull(createImportedManifest);
        _getRestoreDestination = getRestoreDestination;
        _transformImportedFile = transformImportedFile;
        _createImportedManifest = createImportedManifest;
    }

    internal FullBackupInfo GetBackupInfo(string path, FullBackupType backupType)
    {
        var backup = ReadBackup(path, backupType);
        return backup.Status switch
        {
            BackupReadStatus.Missing => new FullBackupInfo(false, false, null, 0, null),
            BackupReadStatus.Valid => new FullBackupInfo(
                true,
                true,
                backup.Manifest!.CreatedUtc,
                backup.Manifest.Entries.Count,
                null),
            _ => new FullBackupInfo(true, false, null, 0, backup.Error)
        };
    }

    internal FullBackupPreview GetBackupPreview(string path, FullBackupType backupType)
    {
        var backup = ReadBackup(path, backupType);
        if (backup.Status == BackupReadStatus.Missing)
            return new FullBackupPreview(false, false, Guid.Empty, null, [], null);
        if (backup.Status != BackupReadStatus.Valid)
            return new FullBackupPreview(true, false, Guid.Empty, null, [], backup.Error);

        var manifest = backup.Manifest!;
        var files = manifest.Entries
            .Select(entry => new BackupFileSummary(
                entry.LogicalPath,
                GetDisplayPath(entry.LogicalPath),
                GetFileCategory(entry.LogicalPath),
                entry.Length,
                entry.Sha256,
                entry.ItemCount,
                entry.ContentLength,
                CompareWithCurrentFile(entry, manifest)))
            .OrderBy(file => file.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FullBackupPreview(
            true,
            true,
            manifest.BackupId,
            manifest.CreatedUtc,
            files,
            null);
    }

    internal BackupFileContent ReadBackupFile(
        string path,
        FullBackupType backupType,
        Guid backupId,
        BackupFileSummary file)
    {
        if (backupId == Guid.Empty || string.IsNullOrWhiteSpace(file.LogicalPath))
            return PreviewSelectionRequired("Select a backup file to preview.");

        var manifestResult = JsonFileStore.Read<FullBackupManifest>(
            Path.Combine(path, ManifestFileName),
            BackupFormat.JsonOptions);
        var manifest = manifestResult.Value;
        if (!manifestResult.Success ||
            manifest is null ||
            manifest.SchemaVersion != BackupSchemaVersion ||
            manifest.Type != backupType ||
            manifest.BackupId != backupId ||
            manifest.Entries is null)
        {
            return BackupChanged("The backup changed after this preview was opened. Close this window and check it again.");
        }

        var entry = manifest.Entries.FirstOrDefault(item =>
            string.Equals(
                NormalizeLogicalPath(item.LogicalPath),
                NormalizeLogicalPath(file.LogicalPath),
                StringComparison.OrdinalIgnoreCase));
        if (entry is null ||
            entry.Length != file.Length ||
            !string.Equals(entry.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return BackupChanged("The selected file changed after this preview was opened. Close this window and check it again.");
        }

        try
        {
            if (entry.Length > MaximumPreviewFileSize)
            {
                return PreviewSelectionRequired(
                    "This file is verified but too large to display in the preview window.");
            }

            var filesDirectory = Path.Combine(path, FilesDirectoryName);
            var filePath = GetContainedPath(filesDirectory, entry.LogicalPath);
            var bytes = ReadFileBytes(filePath);
            VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);

            return new BackupFileContent(
                true,
                false,
                GetContentFormat(entry.LogicalPath),
                bytes,
                null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return BackupChanged("The selected file could not be verified.");
        }
    }

    internal FullBackupPreview GetImportPreview(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new FullBackupPreview(false, false, Guid.Empty, null, [], "The selected backup file could not be found.");

        try
        {
            var archive = ReadBackupArchive(archivePath);
            var importedFiles = InspectImportedFiles(archive);
            var previewManifest = _createImportedManifest(
                archive.Manifest,
                archive.Manifest.BackupId,
                importedFiles);
            var files = importedFiles
                .Zip(previewManifest.Entries)
                .Select(pair => new BackupFileSummary(
                    pair.Second.LogicalPath,
                    GetDisplayPath(pair.Second.LogicalPath),
                    GetFileCategory(pair.Second.LogicalPath),
                    pair.Second.Length,
                    pair.Second.Sha256,
                    pair.Second.ItemCount,
                    pair.Second.ContentLength,
                    CompareWithCurrentFile(pair.Second, previewManifest),
                    pair.First.SourceLogicalPath))
                .OrderBy(file => file.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.DisplayPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new FullBackupPreview(
                true,
                true,
                archive.Manifest.BackupId,
                archive.Manifest.CreatedUtc,
                files,
                null,
                archive.ManifestHash);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return new FullBackupPreview(true, false, Guid.Empty, null, [], "The selected backup could not be read or verified.");
        }
    }

    internal BackupFileContent ReadImportFile(
        string archivePath,
        Guid backupId,
        string verificationToken,
        BackupFileSummary file)
    {
        if (backupId == Guid.Empty || string.IsNullOrWhiteSpace(file.SourceLogicalPath))
            return PreviewSelectionRequired("Select an imported backup file to preview.");

        try
        {
            var archive = ReadBackupArchive(archivePath);
            if (archive.Manifest.BackupId != backupId ||
                !archive.ManifestHash.Equals(verificationToken, StringComparison.OrdinalIgnoreCase))
            {
                return BackupChanged("The imported backup changed after this preview was opened. Close this window and check it again.");
            }

            var sourceEntry = archive.Manifest.Entries.FirstOrDefault(entry =>
                string.Equals(
                    NormalizeLogicalPath(entry.LogicalPath),
                    NormalizeLogicalPath(file.SourceLogicalPath),
                    StringComparison.OrdinalIgnoreCase));
            if (sourceEntry is null)
                return BackupChanged("The selected file is no longer present in the imported backup.");

            var importedFile = ReadImportedFile(archive, sourceEntry);
            if (!string.Equals(importedFile.LogicalPath, file.LogicalPath, StringComparison.OrdinalIgnoreCase) ||
                importedFile.Data.LongLength != file.Length ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(importedFile.Data)),
                    file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return BackupChanged("The selected file changed after this preview was opened. Close this window and check it again.");
            }

            if (importedFile.Data.LongLength > MaximumPreviewFileSize)
                return PreviewSelectionRequired("This file is verified but too large to display in the preview window.");

            return new BackupFileContent(
                true,
                false,
                GetContentFormat(importedFile.LogicalPath),
                importedFile.Data,
                null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return BackupChanged("The selected file could not be verified.");
        }
    }

    internal BackupReadResult ReadBackup(string path, FullBackupType backupType)
    {
        if (!Directory.Exists(path))
            return new BackupReadResult(BackupReadStatus.Missing, backupType, path, null, null);

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return InvalidBackup(backupType, path, "The backup directory is a filesystem link.");

            var rootFiles = Directory.EnumerateFiles(path).Select(Path.GetFileName).ToList();
            var rootDirectories = Directory.EnumerateDirectories(path).Select(Path.GetFileName).ToList();
            if (rootFiles.Count != 1 ||
                !rootFiles[0]!.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                rootDirectories.Count != 1 ||
                !rootDirectories[0]!.Equals(FilesDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return InvalidBackup(backupType, path, "The backup contains unexpected top-level files or directories.");
            }

            var manifestResult = JsonFileStore.Read<FullBackupManifest>(
                Path.Combine(path, ManifestFileName),
                BackupFormat.JsonOptions);
            if (!manifestResult.Success)
                return InvalidBackup(backupType, path, "The backup manifest is missing, unreadable, or invalid.");

            var manifest = manifestResult.Value!;
            if (manifest.SchemaVersion != BackupSchemaVersion)
                return InvalidBackup(backupType, path, "The backup schema is not supported.");
            if (manifest.Type != backupType)
                return InvalidBackup(backupType, path, "The backup type does not match its directory.");
            if (manifest.BackupId == Guid.Empty)
                return InvalidBackup(backupType, path, "The backup ID is missing.");
            if (manifest.Entries is null || manifest.Entries.Count == 0)
                return InvalidBackup(backupType, path, "The backup contains no files.");
            if (manifest.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.LogicalPath)))
                return InvalidBackup(backupType, path, "The backup contains an empty file entry or path.");

            var duplicate = manifest.Entries
                .GroupBy(entry => entry.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return InvalidBackup(backupType, path, $"The manifest repeats '{duplicate.Key}'.");

            var filesDirectory = Path.Combine(path, FilesDirectoryName);
            if ((File.GetAttributes(filesDirectory) & FileAttributes.ReparsePoint) != 0)
                return InvalidBackup(backupType, path, "The backup files directory is a filesystem link.");

            var expectedPaths = new HashSet<string>(
                manifest.Entries.Select(entry => NormalizeLogicalPath(entry.LogicalPath)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (entry.Length < 0 || string.IsNullOrWhiteSpace(entry.Sha256))
                    return InvalidBackup(backupType, path, $"The manifest entry '{entry.LogicalPath}' is incomplete.");

                _ = _getRestoreDestination(entry.LogicalPath, manifest);
                var filePath = GetContainedPath(filesDirectory, entry.LogicalPath);
                if (!File.Exists(filePath))
                    return InvalidBackup(backupType, path, $"The backup file '{entry.LogicalPath}' is missing.");

                var bytes = ReadFileBytes(filePath);
                if (bytes.LongLength != entry.Length ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return InvalidBackup(backupType, path, $"The backup file '{entry.LogicalPath}' failed hash verification.");
                }
            }

            var actualPaths = EnumerateFilesWithoutLinks(filesDirectory)
                .Select(file => NormalizeLogicalPath(Path.GetRelativePath(filesDirectory, file)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedPaths.SetEquals(actualPaths))
                return InvalidBackup(backupType, path, "The backup file list does not match its manifest.");

            return new BackupReadResult(BackupReadStatus.Valid, backupType, path, manifest, null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return InvalidBackup(backupType, path, "The selected backup could not be read or verified.");
        }
    }

    internal void VerifyBackupFolder(string path, FullBackupType backupType)
    {
        var verification = ReadBackup(path, backupType);
        if (verification.Status != BackupReadStatus.Valid)
        {
            throw new FileVerificationException(
                $"The saved {GetBackupName(backupType)} could not be verified: {verification.Error}");
        }
    }

    internal BackupArchive ReadBackupArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected backup file could not be found.", fullPath);

        using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("The selected backup archive is empty.");
        if (archive.Entries.Count > MaximumImportFileCount + 1)
            throw new InvalidDataException("The selected backup contains too many files.");
        if (archive.Entries.Any(entry => string.IsNullOrEmpty(entry.Name)))
            throw new InvalidDataException("The selected backup contains unexpected directory entries.");

        foreach (var entry in archive.Entries)
            ValidateArchivePath(entry.FullName);
        var duplicate = archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"The selected backup repeats '{duplicate.Key}'.");

        var manifestEntry = archive.Entries.SingleOrDefault(entry =>
            entry.FullName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
            throw new InvalidDataException("The selected backup does not contain a manifest.");
        if (manifestEntry.Length > MaximumImportManifestSize)
            throw new InvalidDataException("The selected backup manifest is too large.");

        var manifestBytes = ReadArchiveEntry(manifestEntry, MaximumImportManifestSize);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var manifest = JsonSerializer.Deserialize<FullBackupManifest>(manifestBytes, BackupFormat.JsonOptions)
            ?? throw new InvalidDataException("The selected backup manifest contains no value.");
        ValidateImportedManifest(manifest);

        var expectedPaths = manifest.Entries
            .Select(entry => $"{FilesDirectoryName}/{NormalizeLogicalPath(entry.LogicalPath)}")
            .Append(ManifestFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedPaths.SetEquals(actualPaths))
            throw new InvalidDataException("The selected backup file list does not match its manifest.");

        long totalSize = 0;
        foreach (var file in manifest.Entries)
        {
            var maximumFileSize = Path.GetExtension(file.LogicalPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? MaximumImportJsonFileSize
                : MaximumImportFileSize;
            if (file.Length < 0 || file.Length > maximumFileSize)
                throw new InvalidDataException($"The imported file '{file.LogicalPath}' is too large.");
            if (!IsSha256(file.Sha256))
                throw new InvalidDataException($"The imported file '{file.LogicalPath}' has an invalid hash.");
            totalSize = checked(totalSize + file.Length);
            if (totalSize > MaximumImportTotalSize)
                throw new InvalidDataException("The selected backup is too large to import safely.");

            var entry = FindArchiveEntry(archive.Entries, $"{FilesDirectoryName}/{NormalizeLogicalPath(file.LogicalPath)}");
            if (entry.Length != file.Length)
                throw new InvalidDataException($"The imported file '{file.LogicalPath}' has the wrong size.");
        }

        return new BackupArchive(fullPath, manifest, manifestHash);
    }

    internal ImportedBackupFile ReadImportedFile(BackupArchive source, FullBackupEntry entry)
    {
        using var fileStream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        return ReadImportedFile(archive, source.Manifest, entry);
    }

    internal ImportedBackupFile ReadImportedFile(
        ZipArchive archive,
        FullBackupManifest manifest,
        FullBackupEntry entry)
    {
        var archiveEntry = FindArchiveEntry(archive.Entries, $"{FilesDirectoryName}/{NormalizeLogicalPath(entry.LogicalPath)}");
        var bytes = ReadArchiveEntry(archiveEntry, MaximumImportFileSize);
        VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
        return _transformImportedFile(entry, bytes, manifest);
    }

    private IReadOnlyList<ImportedBackupFileInfo> InspectImportedFiles(BackupArchive source)
    {
        using var fileStream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        var files = source.Manifest.Entries
            .Select(entry => ReadImportedFile(archive, source.Manifest, entry).ToInfo())
            .ToList();
        var duplicate = files.GroupBy(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Multiple imported files map to '{duplicate.Key}'.");
        return files;
    }

    private void ValidateImportedManifest(FullBackupManifest manifest)
    {
        if (manifest.SchemaVersion != BackupSchemaVersion)
            throw new InvalidDataException("The selected backup schema is not supported.");
        if (manifest.Type != FullBackupType.User)
            throw new InvalidDataException("Only a user-created full backup can be imported.");
        if (manifest.BackupId == Guid.Empty)
            throw new InvalidDataException("The selected backup ID is missing.");
        if (string.IsNullOrWhiteSpace(manifest.AppDataRoot) ||
            string.IsNullOrWhiteSpace(manifest.NotesRoot))
        {
            throw new InvalidDataException("The selected backup does not identify its original storage folders.");
        }
        if (!Path.IsPathFullyQualified(manifest.AppDataRoot) ||
            !Path.IsPathFullyQualified(manifest.NotesRoot))
        {
            throw new InvalidDataException("The selected backup contains an invalid original storage folder.");
        }
        if (manifest.Entries is null || manifest.Entries.Count == 0)
            throw new InvalidDataException("The selected backup contains no files.");
        if (manifest.Entries.Count > MaximumImportFileCount)
            throw new InvalidDataException("The selected backup contains too many files.");
        if (manifest.Entries.Any(entry => entry is null))
            throw new InvalidDataException("The selected backup manifest contains an empty file entry.");

        _ = NormalizeBackupDataPath(manifest.AppDataRoot);
        _ = NormalizeBackupDataPath(manifest.NotesRoot);
        var duplicate = manifest.Entries
            .GroupBy(entry => NormalizeLogicalPath(entry.LogicalPath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"The selected backup manifest repeats '{duplicate.Key}'.");

        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.LogicalPath))
                throw new InvalidDataException("The selected backup manifest contains an empty file path.");
            ValidateArchivePath(entry.LogicalPath);
            _ = _getRestoreDestination(entry.LogicalPath, manifest);
            if (entry.ItemCount < 0 || entry.ContentLength < 0)
                throw new InvalidDataException($"The imported file '{entry.LogicalPath}' has invalid metadata.");
        }
    }

    private BackupFileComparison CompareWithCurrentFile(FullBackupEntry entry, FullBackupManifest manifest)
    {
        try
        {
            var bytes = ReadFileBytes(_getRestoreDestination(entry.LogicalPath, manifest));
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return bytes.LongLength == entry.Length &&
                   string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase)
                ? BackupFileComparison.Unchanged
                : BackupFileComparison.Changed;
        }
        catch (FileNotFoundException) { return BackupFileComparison.Missing; }
        catch (DirectoryNotFoundException) { return BackupFileComparison.Missing; }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return BackupFileComparison.Unavailable;
        }
    }

    internal static byte[] ReadFileBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
            throw new IOException($"'{path}' is too large for the current backup implementation.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static void VerifyFileBytes(byte[] bytes, long expectedLength, string expectedHash, string logicalPath)
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.LongLength != expectedLength ||
            !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileVerificationException($"The restored file '{logicalPath}' did not match the verified backup.");
        }
    }

    private static byte[] ReadArchiveEntry(ZipArchiveEntry entry, long maximumSize)
    {
        if (entry.Length < 0 || entry.Length > maximumSize || entry.Length > int.MaxValue)
            throw new InvalidDataException($"The imported file '{entry.FullName}' is too large.");
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"The imported file '{entry.FullName}' is larger than declared.");
        return bytes;
    }

    private static ZipArchiveEntry FindArchiveEntry(IEnumerable<ZipArchiveEntry> entries, string path)
    {
        var normalizedPath = NormalizeLogicalPath(path);
        return entries.SingleOrDefault(entry =>
                   NormalizeLogicalPath(entry.FullName).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException($"The selected backup is missing '{path}'.");
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':'))
            throw new InvalidDataException($"The selected backup contains an unsafe path: '{path}'.");
        var parts = path.Split('/');
        if (parts.Any(part =>
                string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedWindowsFileName(part)))
        {
            throw new InvalidDataException($"The selected backup contains an unsafe path: '{path}'.");
        }
    }

    private static bool IsReservedWindowsFileName(string name)
    {
        var baseName = name.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (baseName.Length == 4 &&
             (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             baseName[3] is >= '1' and <= '9');
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string GetBackupName(FullBackupType backupType) =>
        backupType switch
        {
            FullBackupType.User => "user backup",
            FullBackupType.Recent => "recent automatic backup",
            FullBackupType.BeforeRestore => "before-restore backup",
            _ => throw new ArgumentOutOfRangeException(nameof(backupType))
        };

    private static BackupReadResult InvalidBackup(FullBackupType type, string path, string error) =>
        new(BackupReadStatus.Invalid, type, path, null, error);

    private static BackupFileContent PreviewSelectionRequired(string message) =>
        new(false, false, BackupContentFormat.Text, null, message);

    private static BackupFileContent BackupChanged(string message) =>
        new(false, true, BackupContentFormat.Text, null, message);

    private static bool IsExpectedBackupException(Exception exception) =>
        FileSystemErrors.IsExpected(exception) || exception is JsonException or InvalidDataException;
}

internal enum BackupReadStatus { Missing, Valid, Invalid }

internal sealed record BackupReadResult(
    BackupReadStatus Status,
    FullBackupType Type,
    string Path,
    FullBackupManifest? Manifest,
    string? Error);

internal sealed record BackupArchive(string Path, FullBackupManifest Manifest, string ManifestHash);

internal sealed record ImportedBackupFile(
    string SourceLogicalPath,
    string LogicalPath,
    byte[] Data,
    int? ItemCount,
    long? ContentLength)
{
    internal ImportedBackupFileInfo ToInfo() =>
        new(
            SourceLogicalPath,
            LogicalPath,
            Data.LongLength,
            Convert.ToHexString(SHA256.HashData(Data)),
            ItemCount,
            ContentLength);
}

internal sealed record ImportedBackupFileInfo(
    string SourceLogicalPath,
    string LogicalPath,
    long Length,
    string Sha256,
    int? ItemCount,
    long? ContentLength);
