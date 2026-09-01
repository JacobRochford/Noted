using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noted.Models;

namespace Noted.Services;

internal sealed partial class FullBackupService
{
    private const int MaximumImportFileCount = 10_000;
    private const long MaximumImportFileSize = 256L * 1024 * 1024;
    private const long MaximumImportJsonFileSize = 16L * 1024 * 1024;
    private const long MaximumImportTotalSize = 2L * 1024 * 1024 * 1024;
    private const long MaximumImportManifestSize = 4L * 1024 * 1024;

    internal FullBackupPreview GetBackupImportPreview(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return new FullBackupPreview(
                false,
                false,
                Guid.Empty,
                null,
                [],
                "The selected backup file could not be found.");
        }

        try
        {
            var archive = ReadBackupArchive(archivePath);
            var importedFiles = InspectImportedFiles(archive);
            var previewManifest = CreateImportedManifest(
                archive.Manifest,
                archive.Manifest.BackupId,
                importedFiles);
            var files = previewManifest.Entries
                .Zip(importedFiles)
                .Select(pair => new BackupFileSummary(
                    pair.First.LogicalPath,
                    GetDisplayPath(pair.First.LogicalPath),
                    GetFileCategory(pair.First.LogicalPath),
                    pair.First.Length,
                    pair.First.Sha256,
                    pair.First.ItemCount,
                    pair.First.ContentLength,
                    CompareWithCurrentFile(pair.First, previewManifest),
                    pair.Second.SourceLogicalPath))
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
            System.Diagnostics.Debug.WriteLine(ex);
            return new FullBackupPreview(
                true,
                false,
                Guid.Empty,
                null,
                [],
                ex.Message);
        }
    }

    internal BackupFileContent ReadBackupImportFile(
        string archivePath,
        Guid backupId,
        string verificationToken,
        BackupFileSummary file)
    {
        if (backupId == Guid.Empty || string.IsNullOrWhiteSpace(file.SourceLogicalPath))
        {
            return new BackupFileContent(
                false,
                false,
                BackupContentFormat.Text,
                null,
                "Select an imported backup file to preview.");
        }

        try
        {
            var archive = ReadBackupArchive(archivePath);
            if (archive.Manifest.BackupId != backupId ||
                !archive.ManifestHash.Equals(verificationToken, StringComparison.OrdinalIgnoreCase))
            {
                return ImportFileChanged(
                    "The imported backup changed after this preview was opened. Close this window and check it again.");
            }

            var sourceEntry = archive.Manifest.Entries.FirstOrDefault(entry =>
                string.Equals(
                    NormalizeLogicalPath(entry.LogicalPath),
                    NormalizeLogicalPath(file.SourceLogicalPath),
                    StringComparison.OrdinalIgnoreCase));
            if (sourceEntry is null)
                return ImportFileChanged("The selected file is no longer present in the imported backup.");

            var importedFile = ReadImportedFile(archive, sourceEntry);
            if (!string.Equals(
                    importedFile.LogicalPath,
                    file.LogicalPath,
                    StringComparison.OrdinalIgnoreCase) ||
                importedFile.Data.LongLength != file.Length ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(importedFile.Data)),
                    file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ImportFileChanged(
                    "The selected file changed after this preview was opened. Close this window and check it again.");
            }

            if (importedFile.Data.LongLength > MaximumPreviewFileSize)
            {
                return new BackupFileContent(
                    false,
                    false,
                    BackupContentFormat.Text,
                    null,
                    "This file is verified but too large to display in the preview window.");
            }

            return new BackupFileContent(
                true,
                false,
                GetContentFormat(importedFile.LogicalPath),
                importedFile.Data,
                null);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return ImportFileChanged($"The selected file could not be verified: {ex.Message}");
        }
    }

    internal FullBackupResult ScheduleBackupImportRestore(
        string archivePath,
        Guid expectedBackupId,
        string expectedVerificationToken)
    {
        string? buildPath = null;
        string? pendingPath = null;
        Guid? importId = null;
        try
        {
            if (File.Exists(GetRestoreRequestPath()))
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.User,
                    null,
                    "Another backup restore is already waiting to finish.");
            }

            var interruptedWork = FindInterruptedWork();
            if (interruptedWork.Count > 0)
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.User,
                    null,
                    "The backup cannot be imported while unfinished backup work needs review: " +
                    string.Join(", ", interruptedWork.Select(Path.GetFileName)));
            }

            var archive = ReadBackupArchive(archivePath);
            if (archive.Manifest.BackupId != expectedBackupId ||
                !archive.ManifestHash.Equals(expectedVerificationToken, StringComparison.OrdinalIgnoreCase))
            {
                return new FullBackupResult(
                    FullBackupStatus.Blocked,
                    FullBackupType.User,
                    null,
                    "The selected backup changed after it was previewed. Nothing was imported.");
            }

            importId = Guid.NewGuid();

            Directory.CreateDirectory(_backupDirectory);
            buildPath = Path.Combine(_backupDirectory, $".importing-{importId.Value:N}");
            Directory.CreateDirectory(buildPath);
            var filesDirectory = Path.Combine(buildPath, FilesDirectoryName);
            Directory.CreateDirectory(filesDirectory);
            var importedFiles = WriteImportedFiles(archive, filesDirectory);
            var manifest = CreateImportedManifest(
                archive.Manifest,
                importId.Value,
                importedFiles);

            JsonFileStore.Write(
                Path.Combine(buildPath, ManifestFileName),
                manifest,
                options: BackupJsonOptions);
            VerifyBackupFolder(buildPath, FullBackupType.User);

            pendingPath = GetPendingImportPath(importId.Value);
            Directory.Move(buildPath, pendingPath);
            buildPath = null;
            VerifyBackupFolder(pendingPath, FullBackupType.User);

            var request = new FullBackupRestoreRequest(
                BackupSchemaVersion,
                importId.Value,
                _clock.GetUtcNow().UtcDateTime,
                importId.Value);
            JsonFileStore.Write(
                GetRestoreRequestPath(),
                request,
                options: BackupJsonOptions);

            return new FullBackupResult(
                FullBackupStatus.RestoreScheduled,
                FullBackupType.User,
                pendingPath,
                "The imported backup is verified and ready to restore when Noted closes. Your user backup was not changed.");
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            TryDeleteImportRestoreRequest(importId);
            TryDeleteImportDirectory(buildPath);
            TryDeleteImportDirectory(pendingPath);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                $"The backup import could not be prepared: {ex.Message}");
        }
    }

    private BackupArchive ReadBackupArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected backup file could not be found.", fullPath);

        using var fileStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("The selected backup archive is empty.");
        if (archive.Entries.Count > MaximumImportFileCount + 1)
            throw new InvalidDataException("The selected backup contains too many files.");
        if (archive.Entries.Any(entry => string.IsNullOrEmpty(entry.Name)))
            throw new InvalidDataException("The selected backup contains unexpected directory entries.");

        foreach (var entry in archive.Entries)
            ValidateArchivePath(entry.FullName);

        var duplicate = archive.Entries
            .GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
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
        var manifest = JsonSerializer.Deserialize<FullBackupManifest>(
                manifestBytes,
                BackupJsonOptions)
            ?? throw new InvalidDataException("The selected backup manifest contains no value.");
        ValidateImportedManifest(manifest);

        var expectedPaths = manifest.Entries
            .Select(entry => $"{FilesDirectoryName}/{NormalizeLogicalPath(entry.LogicalPath)}")
            .Append(ManifestFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = archive.Entries
            .Select(entry => entry.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedPaths.SetEquals(actualPaths))
            throw new InvalidDataException("The selected backup file list does not match its manifest.");

        long totalSize = 0;
        foreach (var file in manifest.Entries)
        {
            var maximumFileSize = Path.GetExtension(file.LogicalPath)
                    .Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? MaximumImportJsonFileSize
                : MaximumImportFileSize;
            if (file.Length < 0 || file.Length > maximumFileSize)
                throw new InvalidDataException($"The imported file '{file.LogicalPath}' is too large.");
            if (!IsSha256(file.Sha256))
                throw new InvalidDataException($"The imported file '{file.LogicalPath}' has an invalid hash.");
            totalSize = checked(totalSize + file.Length);
            if (totalSize > MaximumImportTotalSize)
                throw new InvalidDataException("The selected backup is too large to import safely.");

            var entry = FindArchiveEntry(
                archive.Entries,
                $"{FilesDirectoryName}/{NormalizeLogicalPath(file.LogicalPath)}");
            if (entry.Length != file.Length)
                throw new InvalidDataException($"The imported file '{file.LogicalPath}' has the wrong size.");
        }

        return new BackupArchive(fullPath, manifest, manifestHash);
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

        _ = Path.GetFullPath(manifest.AppDataRoot);
        _ = Path.GetFullPath(manifest.NotesRoot);
        var duplicate = manifest.Entries
            .GroupBy(entry => NormalizeLogicalPath(entry.LogicalPath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"The selected backup manifest repeats '{duplicate.Key}'.");

        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.LogicalPath))
                throw new InvalidDataException("The selected backup manifest contains an empty file path.");
            ValidateBackupLogicalPath(entry.LogicalPath, manifest);
            if (entry.ItemCount < 0 || entry.ContentLength < 0)
                throw new InvalidDataException($"The imported file '{entry.LogicalPath}' has invalid metadata.");
        }
    }

    private void ValidateBackupLogicalPath(
        string logicalPath,
        FullBackupManifest manifest)
    {
        ValidateArchivePath(logicalPath);
        _ = GetRestoreDestination(logicalPath, manifest);
    }

    private IReadOnlyList<ImportedBackupFileInfo> InspectImportedFiles(BackupArchive source)
    {
        using var fileStream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        var files = source.Manifest.Entries
            .Select(entry => ReadImportedFile(archive, source.Manifest, entry).ToInfo())
            .ToList();
        var duplicate = files
            .GroupBy(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Multiple imported files map to '{duplicate.Key}'.");
        return files;
    }

    private IReadOnlyList<ImportedBackupFileInfo> WriteImportedFiles(
        BackupArchive source,
        string filesDirectory)
    {
        using var fileStream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        var importedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ImportedBackupFileInfo>(source.Manifest.Entries.Count);
        foreach (var entry in source.Manifest.Entries)
        {
            var file = ReadImportedFile(archive, source.Manifest, entry);
            if (!importedPaths.Add(file.LogicalPath))
                throw new InvalidDataException($"Multiple imported files map to '{file.LogicalPath}'.");

            var destinationPath = GetContainedPath(filesDirectory, file.LogicalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            FileWriter.WriteAllBytes(destinationPath, file.Data);
            var fileInfo = file.ToInfo();
            VerifyImportedFile(destinationPath, fileInfo);
            files.Add(fileInfo);
        }

        return files;
    }

    private static void VerifyImportedFile(
        string path,
        ImportedBackupFileInfo file)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (stream.Length != file.Length ||
            !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileVerificationException(
                $"The imported file '{file.LogicalPath}' did not match the data written to local import storage.");
        }
    }

    private ImportedBackupFile ReadImportedFile(
        BackupArchive source,
        FullBackupEntry entry)
    {
        using var fileStream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        return ReadImportedFile(archive, source.Manifest, entry);
    }

    private ImportedBackupFile ReadImportedFile(
        ZipArchive archive,
        FullBackupManifest manifest,
        FullBackupEntry entry)
    {
        var archiveEntry = FindArchiveEntry(
            archive.Entries,
            $"{FilesDirectoryName}/{NormalizeLogicalPath(entry.LogicalPath)}");
        var bytes = ReadArchiveEntry(archiveEntry, MaximumImportFileSize);
        VerifyFileBytes(bytes, entry.Length, entry.Sha256, entry.LogicalPath);
        return TransformImportedFile(entry, bytes, manifest);
    }

    private ImportedBackupFile TransformImportedFile(
        FullBackupEntry entry,
        byte[] bytes,
        FullBackupManifest manifest)
    {
        var sourcePath = NormalizeLogicalPath(entry.LogicalPath);
        var logicalPath = sourcePath;
        byte[] transformed = bytes;
        BackupContentDetails details;

        if (sourcePath.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
        {
            if (!NoteFileExtensions.IsSupported(sourcePath))
                throw new InvalidDataException($"The imported note '{sourcePath}' has an unsupported file type.");
            details = new BackupContentDetails(null, bytes.LongLength);
        }
        else if (sourcePath.Equals("app/settings.json", StringComparison.OrdinalIgnoreCase))
        {
            ValidateSettings(bytes);
            var settings = JsonNode.Parse(bytes) as JsonObject
                ?? throw new JsonException("The imported Settings file does not contain a JSON object.");
            settings["NotesDirectory"] = _notesDirectory;
            transformed = JsonSerializer.SerializeToUtf8Bytes(settings, BackupJsonOptions);
            details = ValidateSettings(transformed);
        }
        else if (sourcePath.Equals("app/checklist.json", StringComparison.OrdinalIgnoreCase))
        {
            var items = ValidateCollection<List<ChecklistItemState>>(bytes);
            details = new BackupContentDetails(items.Count, null);
        }
        else if (sourcePath.Equals("app/checklist-tabs.json", StringComparison.OrdinalIgnoreCase))
        {
            var tabs = ValidateCollection<List<ChecklistTabState>>(bytes);
            details = new BackupContentDetails(tabs.Count, null);
        }
        else if (sourcePath.Equals("app/dictionary.json", StringComparison.OrdinalIgnoreCase))
        {
            var items = ValidateCollection<List<DictionaryItemState>>(bytes);
            details = new BackupContentDetails(items.Count, null);
        }
        else if (sourcePath.Equals("app/scratchpad.rtf", StringComparison.OrdinalIgnoreCase))
        {
            details = new BackupContentDetails(null, bytes.LongLength);
        }
        else if (sourcePath.Equals("app/session/editor-workspace.json", StringComparison.OrdinalIgnoreCase))
        {
            var session = ValidateEditorSession(bytes);
            if (session.Tabs.Any(tab => tab is null))
                throw new JsonException("The imported editor session contains an empty tab.");
            var importedSession = session with
            {
                Tabs = session.Tabs
                    .Select(tab => tab with
                    {
                        FilePath = RebaseNotePath(tab.FilePath, manifest.NotesRoot)
                    })
                    .ToList(),
                ActiveFilePath = string.IsNullOrWhiteSpace(session.ActiveFilePath)
                    ? null
                    : RebaseNotePath(session.ActiveFilePath, manifest.NotesRoot)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedSession, BackupJsonOptions);
            _ = ValidateEditorSession(transformed);
            details = new BackupContentDetails(null, null);
        }
        else if (sourcePath.StartsWith("app/recovery/micro-scratchpads/", StringComparison.OrdinalIgnoreCase))
        {
            var expectedPath = $"app/recovery/micro-scratchpads/{MiniPadDraftId:N}.json";
            if (!sourcePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The imported backup contains an unsupported MiniPad file: '{sourcePath}'.");
            details = ValidateMiniPadDraft(bytes, sourcePath);
        }
        else if (sourcePath.StartsWith("app/recovery/", StringComparison.OrdinalIgnoreCase))
        {
            if (sourcePath.Split('/').Length != 3 ||
                !Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The imported backup contains an unsupported note recovery file: '{sourcePath}'.");
            }
            details = ValidateNoteDraft(bytes, sourcePath);
            var draft = JsonSerializer.Deserialize<NoteRecoveryDraft>(bytes)
                ?? throw new JsonException("The imported note recovery file contains no value.");
            var importedDraft = draft with
            {
                FilePath = RebaseNotePath(draft.FilePath, manifest.NotesRoot)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedDraft, BackupJsonOptions);
            var fileName = Path.GetFileName(sourcePath).Equals(
                "editor-draft.json",
                StringComparison.OrdinalIgnoreCase)
                ? "editor-draft.json"
                : $"{GetPathHash(importedDraft.FilePath)}.json";
            logicalPath = $"app/recovery/{fileName}";
            details = ValidateNoteDraft(transformed, logicalPath);
        }
        else if (sourcePath.StartsWith("app/note-history/", StringComparison.OrdinalIgnoreCase))
        {
            details = ValidateNoteHistoryEntry(bytes);
            var history = JsonSerializer.Deserialize<ImportedNoteHistoryEntry>(bytes)
                ?? throw new JsonException("The imported note history file contains no value.");
            var sourceParts = sourcePath.Split('/');
            if (sourceParts.Length != 4 ||
                !sourceParts[2].Equals(GetPathHash(history.NotePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The imported note history path does not match its note.");
            }

            var importedHistory = history with
            {
                NotePath = RebaseNotePath(history.NotePath, manifest.NotesRoot)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedHistory, BackupJsonOptions);
            logicalPath = $"app/note-history/{GetPathHash(importedHistory.NotePath)}/{sourceParts[3]}";
            details = ValidateNoteHistoryEntry(transformed);
        }
        else if (sourcePath.StartsWith("app/DeletedNotes/", StringComparison.OrdinalIgnoreCase))
        {
            if (!NoteFileExtensions.IsSupported(sourcePath))
                throw new InvalidDataException($"The imported deleted note '{sourcePath}' has an unsupported file type.");
            details = new BackupContentDetails(null, bytes.LongLength);
        }
        else
        {
            throw new InvalidDataException($"The imported backup contains an unsupported file: '{sourcePath}'.");
        }

        return new ImportedBackupFile(
            sourcePath,
            logicalPath,
            transformed,
            details.ItemCount,
            details.ContentLength);
    }

    private FullBackupManifest CreateImportedManifest(
        FullBackupManifest source,
        Guid backupId,
        IReadOnlyList<ImportedBackupFileInfo> files)
    {
        var placeholder = new FullBackupManifest(
            BackupSchemaVersion,
            backupId,
            FullBackupType.User,
            source.CreatedUtc,
            _appDataDirectory,
            _notesDirectory,
            []);
        var entries = files
            .Select(file => new FullBackupEntry(
                file.LogicalPath,
                GetRestoreDestination(file.LogicalPath, placeholder),
                file.Length,
                file.Sha256,
                file.ItemCount,
                file.ContentLength))
            .ToList();
        return placeholder with { Entries = entries };
    }

    private string RebaseNotePath(string notePath, string oldNotesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notePath);
        if (!Path.IsPathFullyQualified(notePath) ||
            !NoteFileExtensions.IsSupported(notePath))
        {
            throw new InvalidDataException(
                $"The imported note path '{notePath}' is not a supported full note path.");
        }

        var oldRoot = Path.GetFullPath(oldNotesDirectory);
        var sourcePath = Path.GetFullPath(notePath);
        var relativePath = Path.GetRelativePath(oldRoot, sourcePath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The imported note path '{notePath}' is outside the backup's notes folder.");
        }

        return GetContainedPath(_notesDirectory, relativePath);
    }

    private static string GetPathHash(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
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

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\') ||
            path.StartsWith('/') ||
            path.Contains(':'))
        {
            throw new InvalidDataException($"The selected backup contains an unsafe path: '{path}'.");
        }

        var parts = path.Split('/');
        if (parts.Any(part =>
                string.IsNullOrWhiteSpace(part) ||
                part.Equals(".", StringComparison.Ordinal) ||
                part.Equals("..", StringComparison.Ordinal) ||
                part.EndsWith(' ') ||
                part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsReservedWindowsFileName(part)))
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

    private static BackupFileContent ImportFileChanged(string message) =>
        new(
            false,
            true,
            BackupContentFormat.Text,
            null,
            message);

    private string GetPendingImportPath(Guid importId) =>
        Path.Combine(_backupDirectory, $"pending-import-{importId:N}");

    private void TryDeleteImportDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            DeleteImportDirectory(path);
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void TryCleanupFinishedImportFolders()
    {
        if (!Directory.Exists(_backupDirectory))
            return;

        try
        {
            foreach (var path in Directory.EnumerateDirectories(
                         _backupDirectory,
                         ".restored-import-*",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteImportDirectory(path);
            }

            var request = JsonFileStore.Read<FullBackupRestoreRequest>(
                GetRestoreRequestPath(),
                BackupJsonOptions);
            if (request.Status != JsonFileReadStatus.Missing && !request.Success)
                return;

            var activeImportPath = request.Value?.ImportId is Guid importId
                ? GetPendingImportPath(importId)
                : null;
            foreach (var path in Directory.EnumerateDirectories(
                         _backupDirectory,
                         "pending-import-*",
                         SearchOption.TopDirectoryOnly))
            {
                if (activeImportPath is null || !PathsEqual(path, activeImportPath))
                    TryDeleteImportDirectory(path);
            }
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void TryDeleteImportRestoreRequest(Guid? importId)
    {
        if (!importId.HasValue)
            return;

        var requestPath = GetRestoreRequestPath();
        try
        {
            var request = JsonFileStore.Read<FullBackupRestoreRequest>(
                requestPath,
                BackupJsonOptions);
            if (request.Status == JsonFileReadStatus.Missing ||
                !request.Success ||
                request.Value?.ImportId == importId)
            {
                FileWriter.DeleteIfExists(requestPath);
            }
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void DeleteImportDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithSeparator = Path.EndsInDirectorySeparator(_backupDirectory)
            ? _backupDirectory
            : _backupDirectory + Path.DirectorySeparatorChar;
        var name = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            (!name.StartsWith(".importing-", StringComparison.OrdinalIgnoreCase) &&
             !name.StartsWith("pending-import-", StringComparison.OrdinalIgnoreCase) &&
             !name.StartsWith(".restored-import-", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("The import folder is outside Noted's backup storage.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private sealed record BackupArchive(
        string Path,
        FullBackupManifest Manifest,
        string ManifestHash);

    private sealed record ImportedBackupFile(
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

    private sealed record ImportedBackupFileInfo(
        string SourceLogicalPath,
        string LogicalPath,
        long Length,
        string Sha256,
        int? ItemCount,
        long? ContentLength);

    private sealed record ImportedNoteHistoryEntry(
        int SchemaVersion,
        string NotePath,
        string Content,
        DateTime SavedUtc);
}
