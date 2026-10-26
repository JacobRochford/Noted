using System.IO;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed partial class BackupArchiveReader
{
    internal string? GetExportIneligibility(string path, FullBackupManifest manifest)
    {
        if (GetLegacyExportIneligibility(path, manifest) is null)
            return null;
        try
        {
            ValidateImportedManifest(manifest with { Type = FullBackupType.User }, multipart: true);
            MultipartBackupContainer.ValidateCapacity(manifest);
            var files = new List<ImportedBackupFileInfo>();
            foreach (var entry in manifest.Entries)
            {
                var filePath = GetContainedPath(Path.Combine(path, FilesDirectoryName), entry.LogicalPath);
                if (IsOpaqueMultipartFile(entry.LogicalPath))
                {
                    VerifyFile(filePath, entry);
                    files.Add(OpaqueFileInfo(entry));
                }
                else
                {
                    ValidateManagedSize(entry);
                    files.Add(ValidateImportedFile(manifest, entry, ReadFileBytes(filePath)).ToInfo());
                }
            }
            ValidateImportedFileDestinations(files);
            return null;
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            return ex.Message;
        }
    }

    internal IReadOnlyList<ImportedBackupFileInfo> ProcessMultipartFiles(BackupArchive source,
        string? filesDirectory = null, CancellationToken cancellationToken = default)
    {
        var files = new List<ImportedBackupFileInfo>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var read = MultipartBackupContainer.Read(source.Path, (manifest, entry, stream) =>
        {
            ValidateArchivePath(entry.LogicalPath);
            _ = _getRestoreDestination(entry.LogicalPath, manifest);
            ImportedBackupFileInfo info;
            if (IsOpaqueMultipartFile(entry.LogicalPath))
            {
                info = OpaqueFileInfo(entry);
                if (!paths.Add(info.LogicalPath))
                    throw new InvalidDataException($"Multiple imported files map to '{info.LogicalPath}'.");
                if (filesDirectory is not null)
                {
                    var destination = PrepareImportedDestination(filesDirectory, info.LogicalPath);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.WriteThrough);
                    stream.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
            }
            else
            {
                ValidateManagedSize(entry);
                var bytes = new byte[checked((int)entry.Length)];
                stream.ReadExactly(bytes);
                var imported = ValidateImportedFile(manifest, entry, bytes);
                info = imported.ToInfo();
                if (!paths.Add(info.LogicalPath))
                    throw new InvalidDataException($"Multiple imported files map to '{info.LogicalPath}'.");
                if (filesDirectory is not null)
                    FileWriter.WriteAllBytes(PrepareImportedDestination(filesDirectory, info.LogicalPath), imported.Data);
            }
            files.Add(info);
        }, cancellationToken);
        if (read.Manifest.BackupId != source.Manifest.BackupId || read.ManifestHash != source.ManifestHash)
            throw new InvalidDataException("The multipart backup changed after it was checked.");
        ValidateImportedManifest(read.Manifest, multipart: true);
        var rebased = _createImportedManifest(read.Manifest, read.Manifest.BackupId, files);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rebased.Entries)
        {
            var destination = _getRestoreDestination(entry.LogicalPath, rebased);
            if (entry.LogicalPath.StartsWith(PersistencePaths.NotesBackupPrefix, StringComparison.OrdinalIgnoreCase))
                destination = FileSystemLinkValidator.ValidateWithinRoot(rebased.NotesRoot, destination);
            FileSystemLinkValidator.ValidatePath(destination);
            destination = Path.Combine(FileSystemLinkValidator.ResolveDirectory(Path.GetDirectoryName(destination)!), Path.GetFileName(destination));
            if (File.Exists(destination))
                destination = FileSystemLinkValidator.ResolveFile(destination);
            if (!destinations.Add(destination))
                throw new InvalidDataException($"Multiple imported files map to '{destination}'.");
        }
        foreach (var destination in destinations)
        {
            for (var parent = Path.GetDirectoryName(destination); parent is not null; parent = Path.GetDirectoryName(parent))
                if (destinations.Contains(parent))
                    throw new InvalidDataException("An imported file also appears as a destination folder.");
        }
        return files;
    }

    private ImportedBackupFile ReadMultipartPreviewFile(BackupArchive source, FullBackupEntry selected)
    {
        if (selected.Length > MaximumPreviewFileSize)
            throw new InvalidDataException("The file is too large to preview.");
        ImportedBackupFile? result = null;
        var read = MultipartBackupContainer.Read(source.Path, (manifest, entry, stream) =>
        {
            if (entry.LogicalPath != selected.LogicalPath) return;
            if (entry.Length > MaximumPreviewFileSize)
                throw new InvalidDataException("The selected file changed after it was checked.");
            var bytes = new byte[(int)entry.Length];
            stream.ReadExactly(bytes);
            result = IsOpaqueMultipartFile(entry.LogicalPath)
                ? new ImportedBackupFile(entry.LogicalPath, entry.LogicalPath, bytes, null, entry.Length)
                : ValidateImportedFile(manifest, entry, bytes);
        });
        if (read.ManifestHash != source.ManifestHash || result is null)
            throw new InvalidDataException("The selected backup changed after it was checked.");
        return result;
    }

    private static string PrepareImportedDestination(string root, string logicalPath)
    {
        var destination = GetContainedPath(root, logicalPath);
        FileSystemLinkValidator.ValidatePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return destination;
    }

    private static bool IsOpaqueMultipartFile(string path) =>
        path.StartsWith(PersistencePaths.NotesBackupPrefix, StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWith(PersistencePaths.DeletedNotesBackupPrefix, StringComparison.OrdinalIgnoreCase) && NoteFileExtensions.IsSupported(path)) ||
        BackupContentDefinitions.Find(path)?.Content == FixedBackupContent.Scratchpad;

    private static ImportedBackupFileInfo OpaqueFileInfo(FullBackupEntry entry) =>
        new(entry.LogicalPath, entry.LogicalPath, entry.Length, entry.Sha256, null, entry.Length);

    private static void ValidateManagedSize(FullBackupEntry entry)
    {
        if (entry.Length > MaximumImportJsonFileSize)
            throw new InvalidDataException($"Managed application file '{entry.LogicalPath}' exceeds the 16 MiB structured-data limit. Notes-folder files are streamed without this limit.");
    }
}
