using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noted.Models;
using static Noted.Services.BackupContentCatalog;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed partial class FullBackupService
{
    internal FullBackupPreview GetBackupImportPreview(string archivePath) =>
        _archiveReader.GetImportPreview(archivePath);

    internal BackupFileContent ReadBackupImportFile(
        string archivePath,
        Guid backupId,
        string verificationToken,
        BackupFileSummary file) =>
        _archiveReader.ReadImportFile(archivePath, backupId, verificationToken, file);

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

            var archive = _archiveReader.ReadBackupArchive(archivePath);
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
                options: BackupFormat.JsonOptions);
            _archiveReader.VerifyBackupFolder(buildPath, FullBackupType.User);

            pendingPath = GetPendingImportPath(importId.Value);
            Directory.Move(buildPath, pendingPath);
            buildPath = null;
            _archiveReader.VerifyBackupFolder(pendingPath, FullBackupType.User);

            var request = new FullBackupRestoreRequest(
                BackupSchemaVersion,
                importId.Value,
                _clock.GetUtcNow().UtcDateTime,
                importId.Value);
            JsonFileStore.Write(
                GetRestoreRequestPath(),
                request,
                options: BackupFormat.JsonOptions);

            return new FullBackupResult(
                FullBackupStatus.RestoreScheduled,
                FullBackupType.User,
                pendingPath,
                "The imported backup is verified and ready to restore when Noted closes. Your user backup was not changed.");
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            TryDeleteImportRestoreRequest(importId);
            TryDeleteImportDirectory(buildPath);
            TryDeleteImportDirectory(pendingPath);
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                "The backup import could not be prepared.");
        }
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
            var file = _archiveReader.ReadImportedFile(archive, source.Manifest, entry);
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
            transformed = JsonSerializer.SerializeToUtf8Bytes(settings, BackupFormat.JsonOptions);
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
                    : RebaseNotePath(session.ActiveFilePath, manifest.NotesRoot),
                SecondaryFilePath = string.IsNullOrWhiteSpace(session.SecondaryFilePath)
                    ? null
                    : RebaseNotePath(session.SecondaryFilePath, manifest.NotesRoot)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedSession, BackupFormat.JsonOptions);
            _ = ValidateEditorSession(transformed);
            details = new BackupContentDetails(null, null);
        }
        else if (sourcePath.StartsWith("app/recovery/micro-scratchpads/", StringComparison.OrdinalIgnoreCase))
        {
            var expectedPath = $"app/recovery/micro-scratchpads/{s_miniPadDraftId:N}.json";
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
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedDraft, BackupFormat.JsonOptions);
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
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedHistory, BackupFormat.JsonOptions);
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
        if (string.IsNullOrWhiteSpace(notePath))
            throw new InvalidDataException("The imported note path is empty.");
        if (!Path.IsPathFullyQualified(notePath) ||
            !NoteFileExtensions.IsSupported(notePath))
        {
            throw new InvalidDataException(
                $"The imported note path '{notePath}' is not a supported full note path.");
        }

        var oldRoot = Path.GetFullPath(oldNotesDirectory);
        var sourcePath = NormalizeBackupDataPath(notePath);
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
        var normalizedPath = NormalizeBackupDataPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

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
            ExceptionDiagnostics.Record(ex);
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
                BackupFormat.JsonOptions);
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
            ExceptionDiagnostics.Record(ex);
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
                BackupFormat.JsonOptions);
            if (request.Status == JsonFileReadStatus.Missing ||
                !request.Success ||
                request.Value?.ImportId == importId)
            {
                FileWriter.DeleteIfExists(requestPath);
            }
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
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

    private sealed record ImportedNoteHistoryEntry(
        int SchemaVersion,
        string NotePath,
        string Content,
        DateTime SavedUtc);
}
