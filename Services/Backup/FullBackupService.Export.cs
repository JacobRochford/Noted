using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed record BackupExportResult(
    bool Success,
    string? ExportPath,
    string Message,
    string? Warning = null);

internal sealed partial class FullBackupService
{
    internal BackupExportResult ExportUserBackup(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var exportPath = Path.GetFullPath(destinationPath);
        var exportDirectory = Path.GetDirectoryName(exportPath)
            ?? throw new ArgumentException("The export path does not contain a directory.", nameof(destinationPath));
        if (string.IsNullOrWhiteSpace(Path.GetFileName(exportPath)))
            throw new ArgumentException("The export path does not contain a file name.", nameof(destinationPath));
        if (PathsEqual(exportDirectory, _backupDirectory) ||
            IsPathWithin(exportDirectory, _backupDirectory))
        {
            return new BackupExportResult(
                false,
                null,
                "Choose an export folder outside Noted's local backup folder.");
        }

        var tempPath = Path.Combine(
            exportDirectory,
            $".{Path.GetFileName(exportPath)}.{Guid.NewGuid():N}.tmp");
        string? rollbackPath = null;

        try
        {
            FileSystemLinkValidator.ValidateDirectory(_backupDirectory);
            var resolvedBackupDirectory = FileSystemLinkValidator.ResolveDirectory(_backupDirectory);
            exportDirectory = FileSystemLinkValidator.ResolveDirectory(exportDirectory);
            if (PathsEqual(exportDirectory, resolvedBackupDirectory) || IsPathWithin(exportDirectory, resolvedBackupDirectory))
                return new BackupExportResult(false, null, "Choose an export folder outside Noted's local backup folder.");
            exportPath = Path.Combine(exportDirectory, Path.GetFileName(exportPath));
            tempPath = Path.Combine(exportDirectory, Path.GetFileName(tempPath));
            FileSystemLinkValidator.ValidatePath(exportPath);
            if (!Directory.Exists(exportDirectory))
            {
                return new BackupExportResult(
                    false,
                    null,
                    "The selected export folder no longer exists.");
            }

            var backupPath = GetBackupPath(FullBackupType.User);
            var backup = _archiveReader.ReadBackup(backupPath, FullBackupType.User);
            if (backup.Status != BackupReadStatus.Valid)
            {
                return new BackupExportResult(
                    false,
                    null,
                    backup.Status == BackupReadStatus.Missing
                        ? "Create a backup before exporting it."
                        : $"The backup could not be exported because it is invalid: {backup.Error}");
            }

            FileSystemLinkValidator.ValidatePath(tempPath);
            WriteBackupExport(tempPath, backup);
            rollbackPath = PublishBackupExport(tempPath, exportPath, path => VerifyBackupExport(path, backup));

            string? warning = null;
            if (rollbackPath is not null)
            {
                try
                {
                    FileSystemLinkValidator.ValidatePath(rollbackPath);
                    FileWriter.DeleteIfExists(rollbackPath);
                }
                catch (Exception ex) when (IsExpectedBackupException(ex))
                {
                    ExceptionDiagnostics.Record(ex);
                    warning = "The export is verified, but its temporary rollback file could not be removed.";
                }
            }

            return new BackupExportResult(
                true,
                exportPath,
                $"Exported the verified backup to '{exportPath}'.",
                warning);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            TryDeleteTemporaryExport(tempPath);
            return new BackupExportResult(
                false,
                null,
                ex is InvalidDataException
                    ? $"The backup could not be exported: {ex.Message} Your local backup was not changed."
                    : "The backup could not be exported.");
        }
    }

    internal static string? PublishBackupExport(string tempPath, string exportPath, Action<string> verify)
    {
        verify(tempPath);

        string? rollbackPath = null;
        FileSystemLinkValidator.ValidatePath(exportPath);
        FileSystemLinkValidator.ValidatePath(tempPath);
        if (File.Exists(exportPath))
        {
            rollbackPath = Path.Combine(
                Path.GetDirectoryName(exportPath)!,
                $".{Path.GetFileName(exportPath)}.{Guid.NewGuid():N}.rollback");
            FileSystemLinkValidator.ValidatePath(rollbackPath);
            File.Replace(tempPath, exportPath, rollbackPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, exportPath);
        }

        try
        {
            verify(exportPath);
        }
        catch
        {
            PreserveFailedExport(exportPath, rollbackPath);
            throw;
        }

        return rollbackPath;
    }

    private static void WriteBackupExport(
        string archivePath,
        BackupReadResult backup)
    {
        var manifest = backup.Manifest
            ?? throw new FileVerificationException("The verified backup manifest is unavailable.");
        var filesDirectory = Path.Combine(backup.Path, FilesDirectoryName);

        using var fileStream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        using (var archive = new ZipArchive(
                   fileStream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            AddArchiveFile(
                archive,
                ManifestFileName,
                Path.Combine(backup.Path, ManifestFileName));
            foreach (var entry in manifest.Entries
                         .OrderBy(item => item.LogicalPath, StringComparer.OrdinalIgnoreCase))
            {
                AddArchiveFile(
                    archive,
                    $"{FilesDirectoryName}/{NormalizeLogicalPath(entry.LogicalPath)}",
                    GetContainedPath(filesDirectory, entry.LogicalPath));
            }
        }

        fileStream.Flush(flushToDisk: true);
    }

    private static void AddArchiveFile(
        ZipArchive archive,
        string archivePath,
        string sourcePath)
    {
        var entry = archive.CreateEntry(
            NormalizeLogicalPath(archivePath),
            CompressionLevel.Fastest);
        FileSystemLinkValidator.ValidatePath(sourcePath);
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private void VerifyBackupExport(
        string archivePath,
        BackupReadResult backup)
    {
        var manifest = backup.Manifest
            ?? throw new FileVerificationException("The verified backup manifest is unavailable.");
        var importedArchive = _archiveReader.ReadBackupArchive(archivePath);
        FileSystemLinkValidator.ValidatePath(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToList();
        var duplicate = entries
            .GroupBy(entry => NormalizeLogicalPath(entry.FullName), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"The exported archive repeats '{duplicate.Key}'.");
        }

        var expectedPaths = manifest.Entries
            .Select(entry => $"{FilesDirectoryName}/{NormalizeLogicalPath(entry.LogicalPath)}")
            .Append(ManifestFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = entries
            .Select(entry => NormalizeLogicalPath(entry.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedPaths.SetEquals(actualPaths))
            throw new InvalidDataException("The exported archive file list is incomplete or contains unexpected files.");

        var manifestEntry = FindArchiveEntry(entries, ManifestFileName);
        if (!StreamsMatch(
                Path.Combine(backup.Path, ManifestFileName),
                manifestEntry))
        {
            throw new InvalidDataException("The exported manifest does not match the verified backup.");
        }

        foreach (var file in manifest.Entries)
        {
            var archiveEntry = FindArchiveEntry(
                entries,
                $"{FilesDirectoryName}/{NormalizeLogicalPath(file.LogicalPath)}");
            if (archiveEntry.Length != file.Length)
            {
                throw new InvalidDataException(
                    $"The exported file '{file.LogicalPath}' has the wrong size.");
            }

            using var stream = archiveEntry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The exported file '{file.LogicalPath}' failed hash verification.");
            }
        }

        _archiveReader.InspectImportedFiles(importedArchive);
    }

    private static ZipArchiveEntry FindArchiveEntry(
        IEnumerable<ZipArchiveEntry> entries,
        string path)
    {
        var normalizedPath = NormalizeLogicalPath(path);
        return entries.Single(entry =>
            string.Equals(
                NormalizeLogicalPath(entry.FullName),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool StreamsMatch(
        string sourcePath,
        ZipArchiveEntry archiveEntry)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length != archiveEntry.Length)
            return false;

        FileSystemLinkValidator.ValidatePath(sourcePath);
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archived = archiveEntry.Open();
        return SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(archived));
    }

    private static void PreserveFailedExport(
        string exportPath,
        string? rollbackPath)
    {
        var failedPath = Path.Combine(
            Path.GetDirectoryName(exportPath)!,
            $".{Path.GetFileName(exportPath)}.{Guid.NewGuid():N}.failed");
        FileSystemLinkValidator.ValidatePath(exportPath);
        FileSystemLinkValidator.ValidatePath(failedPath);
        if (rollbackPath is not null)
            FileSystemLinkValidator.ValidatePath(rollbackPath);
        if (rollbackPath is not null && File.Exists(rollbackPath))
        {
            File.Replace(
                rollbackPath,
                exportPath,
                failedPath,
                ignoreMetadataErrors: true);
            return;
        }

        if (File.Exists(exportPath))
            File.Move(exportPath, failedPath);
    }

    private static void TryDeleteTemporaryExport(string path)
    {
        try
        {
            FileSystemLinkValidator.ValidatePath(path);
            FileWriter.DeleteIfExists(path);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
        }
    }
}
