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
    internal FullBackupPreview GetBackupImportPreview(string archivePath)
    {
        var preview = _archiveReader.GetImportPreview(archivePath);
        if (!preview.IsValid)
            return preview;
        try
        {
            foreach (var file in preview.Files)
                ValidateImportedDraftCollisionByLogicalPath(file.LogicalPath);
            return preview;
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            return preview with { IsValid = false, Error = ex.Message };
        }
    }

    internal BackupFileContent ReadBackupImportFile(
        string archivePath,
        Guid backupId,
        string verificationToken,
        BackupFileSummary file) =>
        _archiveReader.ReadImportFile(archivePath, backupId, verificationToken, file);

    internal FullBackupResult ScheduleBackupImportRestore(
        string archivePath,
        Guid expectedBackupId,
        string expectedVerificationToken,
        CancellationToken cancellationToken = default)
    {
        string? buildPath = null;
        string? pendingPath = null;
        Guid? importId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemLinkValidator.ValidatePath(GetRestoreRequestPath());
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

            var archive = _archiveReader.ReadBackupArchive(archivePath, cancellationToken);
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

            // check rebased paths and content before creating restore files.
            var inspectedFiles = _archiveReader.InspectImportedFiles(archive, cancellationToken);
            foreach (var file in inspectedFiles)
                ValidateImportedDraftCollisionByLogicalPath(file.LogicalPath);

            FileSystemLinkValidator.ValidateDirectory(_backupDirectory);

            Directory.CreateDirectory(_backupDirectory);
            buildPath = Path.Combine(_backupDirectory, $"{ImportingDirectoryPrefix}{importId.Value:N}");
            FileSystemLinkValidator.ValidateDirectory(buildPath);
            Directory.CreateDirectory(buildPath);
            var filesDirectory = Path.Combine(buildPath, FilesDirectoryName);
            FileSystemLinkValidator.ValidateDirectory(filesDirectory);
            Directory.CreateDirectory(filesDirectory);
            var importedFiles = WriteImportedFiles(archive, filesDirectory, cancellationToken);
            var manifest = CreateImportedManifest(
                archive.Manifest,
                importId.Value,
                importedFiles);

            FileSystemLinkValidator.ValidatePath(GetRestoreRequestPath());
            FileSystemLinkValidator.ValidatePath(Path.Combine(buildPath, ManifestFileName));
            JsonFileStore.Write(
                Path.Combine(buildPath, ManifestFileName),
                manifest,
                options: BackupFormat.JsonOptions);
            _archiveReader.VerifyBackupFolder(buildPath, FullBackupType.User);

            pendingPath = GetPendingImportPath(importId.Value);
            FileSystemLinkValidator.ValidateTree(buildPath);
            FileSystemLinkValidator.ValidateDirectory(pendingPath);
            Directory.Move(buildPath, pendingPath);
            buildPath = null;
            _archiveReader.VerifyBackupFolder(pendingPath, FullBackupType.User);

            cancellationToken.ThrowIfCancellationRequested();

            var request = new FullBackupRestoreRequest(
                BackupSchemaVersion,
                importId.Value,
                _clock.GetUtcNow().UtcDateTime,
                importId.Value);
            FileSystemLinkValidator.ValidatePath(GetRestoreRequestPath());
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
        catch (Exception ex) when (IsExpectedBackupException(ex) || ex is OperationCanceledException)
        {
            ExceptionDiagnostics.Record(ex);
            TryDeleteImportRestoreRequest(importId);
            var cleanupWarning = string.Join(" ", new[] { TryDeleteImportDirectory(buildPath), TryDeleteImportDirectory(pendingPath) }
                .Where(message => message is not null));
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                ex is OperationCanceledException ? "Backup import was cancelled."
                    : ex is InvalidDataException ? $"The backup import could not be prepared: {ex.Message}"
                    : "The backup import could not be prepared.",
                string.IsNullOrEmpty(cleanupWarning) ? null : cleanupWarning);
        }
    }

    private IReadOnlyList<ImportedBackupFileInfo> WriteImportedFiles(
        BackupArchive source,
        string filesDirectory,
        CancellationToken cancellationToken)
    {
        if (source.IsMultipart)
            return _archiveReader.ProcessMultipartFiles(source, filesDirectory, cancellationToken);
        FileSystemLinkValidator.ValidatePath(source.Path);
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
            cancellationToken.ThrowIfCancellationRequested();
            var file = _archiveReader.ReadImportedFile(archive, source.Manifest, entry);
            if (!importedPaths.Add(file.LogicalPath))
                throw new InvalidDataException($"Multiple imported files map to '{file.LogicalPath}'.");

            var destinationPath = GetContainedPath(filesDirectory, file.LogicalPath);
            FileSystemLinkValidator.ValidateDirectory(Path.GetDirectoryName(destinationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            FileSystemLinkValidator.ValidatePath(destinationPath);
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
        FileSystemLinkValidator.ValidatePath(path);
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
        var fixedFile = BackupContentDefinitions.Find(sourcePath);
        byte[] transformed = bytes;
        BackupContentDetails details;

        if (sourcePath.StartsWith(PersistencePaths.NotesBackupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!NoteFileExtensions.IsSupported(sourcePath))
                throw new InvalidDataException($"The imported note '{sourcePath}' has an unsupported file type.");
            details = new BackupContentDetails(null, bytes.LongLength);
        }
        else if (fixedFile?.Content == FixedBackupContent.Settings)
        {
            ValidateSettings(bytes);
            var settings = JsonNode.Parse(bytes) as JsonObject
                ?? throw new JsonException("The imported Settings file does not contain a JSON object.");
            settings["NotesDirectory"] = _notesDirectory;
            transformed = JsonSerializer.SerializeToUtf8Bytes(settings, BackupFormat.JsonOptions);
            details = ValidateSettings(transformed);
        }
        else if (fixedFile?.Content == FixedBackupContent.EditorSession)
        {
            var session = ValidateEditorSession(bytes);
            var importedSession = session with
            {
                Tabs = session.Tabs
                    .Select(tab => tab with
                    {
                        FilePath = RebaseNotePath(tab.FilePath, manifest.NotesRoot, manifest.AppDataRoot)
                    })
                    .ToList(),
                ActiveFilePath = string.IsNullOrWhiteSpace(session.ActiveFilePath)
                    ? null
                    : RebaseNotePath(session.ActiveFilePath, manifest.NotesRoot, manifest.AppDataRoot),
                SecondaryFilePath = string.IsNullOrWhiteSpace(session.SecondaryFilePath)
                    ? null
                    : RebaseNotePath(session.SecondaryFilePath, manifest.NotesRoot, manifest.AppDataRoot),
                CompletedDraftPaths = session.CompletedDraftPaths
                    .Select(path => RebaseNotePath(path, manifest.NotesRoot, manifest.AppDataRoot)).ToArray(),
                CompletedDraftTransitions = session.CompletedDraftTransitions.ToDictionary(
                    pair => RebaseNotePath(pair.Key, manifest.NotesRoot, manifest.AppDataRoot), pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedSession);
            _ = ValidateEditorSession(transformed);
            details = new BackupContentDetails(null, null);
        }
        else if (fixedFile is not null)
        {
            details = ValidateFixedFile(fixedFile.Content, bytes);
        }
        else if (NoteDraftStorage.IsBackupPath(sourcePath))
        {
            var parts = sourcePath.Split('/');
            if (parts.Length != 3 || !parts[2].EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                !parts[2].EndsWith(".txt.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The imported Quick Note has an unsupported storage path.");
            if (parts[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var state = JsonSerializer.Deserialize<NoteDraftState>(bytes)
                    ?? throw new InvalidDataException("The imported Quick Note metadata is empty.");
                NoteDraftStorage.ValidateState(state);
                if (state.SavedFilePath is not null)
                    state = state with { SavedFilePath = RebaseNotePath(state.SavedFilePath, manifest.NotesRoot, manifest.AppDataRoot) };
                transformed = JsonSerializer.SerializeToUtf8Bytes(state);
                details = new BackupContentDetails(null, state.InitialContent.Length);
            }
            else
                details = new BackupContentDetails(null, TextFileCodec.Decode(bytes).Content.Length);
        }
        else if (MiniPadStorage.IsBackupPath(sourcePath))
        {
            if (sourcePath.Split('/').Length != 4 ||
                !Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The imported backup contains an unsupported MiniPad file: '{sourcePath}'.");
            details = ValidateMiniPadDraft(bytes, sourcePath);
            logicalPath = MiniPadStorage.NormalizeBackupPath(sourcePath);
        }
        else if (sourcePath.StartsWith(PersistencePaths.RecoveryBackupPrefix, StringComparison.OrdinalIgnoreCase))
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
                FilePath = RebaseNotePath(draft.FilePath, manifest.NotesRoot, manifest.AppDataRoot)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedDraft, BackupFormat.JsonOptions);
            var fileName = Path.GetFileName(sourcePath).Equals(
                PersistencePaths.LegacyEditorDraftFile,
                StringComparison.OrdinalIgnoreCase)
                ? PersistencePaths.LegacyEditorDraftFile
                : $"{NotePathIdentity.GetHash(NormalizeBackupDataPath(importedDraft.FilePath))}.json";
            logicalPath = $"{PersistencePaths.RecoveryBackupPrefix}{fileName}";
            details = ValidateNoteDraft(transformed, logicalPath);
        }
        else if (sourcePath.StartsWith(PersistencePaths.NoteHistoryBackupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            details = ValidateNoteHistoryEntry(bytes);
            var history = JsonSerializer.Deserialize<ImportedNoteHistoryEntry>(bytes)
                ?? throw new JsonException("The imported note history file contains no value.");
            var sourceParts = sourcePath.Split('/');
            if (sourceParts.Length != 4 ||
                !sourceParts[2].Equals(NotePathIdentity.GetHash(NormalizeBackupDataPath(history.NotePath)), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The imported note history path does not match its note.");
            }

            var importedHistory = history with
            {
                NotePath = RebaseNotePath(history.NotePath, manifest.NotesRoot, manifest.AppDataRoot)
            };
            transformed = JsonSerializer.SerializeToUtf8Bytes(importedHistory, BackupFormat.JsonOptions);
            logicalPath = $"{PersistencePaths.NoteHistoryBackupPrefix}{NotePathIdentity.GetHash(NormalizeBackupDataPath(importedHistory.NotePath))}/{sourceParts[3]}";
            details = ValidateNoteHistoryEntry(transformed);
        }
        else if (sourcePath.StartsWith(PersistencePaths.DeletedNotesBackupPrefix, StringComparison.OrdinalIgnoreCase))
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

    private string RebaseNotePath(string notePath, string oldNotesDirectory, string? oldAppDataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(notePath))
            throw new InvalidDataException("The imported note path is empty.");
        if (!Path.IsPathFullyQualified(notePath) ||
            !NoteFileExtensions.IsSupported(notePath))
        {
            throw new InvalidDataException(
                $"The imported note path '{notePath}' is not a supported full note path.");
        }

        var sourcePath = NormalizeBackupDataPath(notePath);
        if (oldAppDataDirectory is not null)
        {
            var draftRoot = Path.Combine(Path.GetFullPath(oldAppDataDirectory), PersistencePaths.NoteDraftsDirectory);
            if (NoteDraftStorage.IsWithinDirectory(sourcePath, draftRoot))
            {
                if (!string.Equals(Path.GetDirectoryName(sourcePath), draftRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetExtension(sourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The imported Quick Note reference is invalid.");
                var rebased = Path.Combine(_appDataDirectory, PersistencePaths.NoteDraftsDirectory, Path.GetFileName(sourcePath));
                return rebased;
            }
        }
        var oldRoot = Path.GetFullPath(oldNotesDirectory);
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

    private void ValidateImportedDraftCollision(string path)
    {
        var recovery = new NoteRecoveryService(_appDataDirectory).GetDraftFilePath(path);
        foreach (var local in new[] { path, NoteDraftStorage.GetMetadataPath(path),
            NoteDraftStorage.GetMetadataPath(path) + ".bak", recovery, recovery + ".bak" })
        {
            FileSystemLinkValidator.ValidatePath(local);
            if (File.Exists(local) || Directory.Exists(local))
                throw new InvalidDataException($"The imported Quick Note conflicts with local draft '{path}'. Nothing was restored.");
        }
    }

    private void ValidateImportedDraftCollisionByLogicalPath(string logicalPath)
    {
        if (!NoteDraftStorage.IsBackupPath(logicalPath))
            return;
        var name = logicalPath.Replace('\\', '/').Split('/')[2];
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        ValidateImportedDraftCollision(Path.Combine(_appDataDirectory, PersistencePaths.NoteDraftsDirectory, name));
    }

    private string GetPendingImportPath(Guid importId) =>
        Path.Combine(_backupDirectory, $"{PendingImportDirectoryPrefix}{importId:N}");

    private string? TryDeleteImportDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        try
        {
            DeleteImportDirectory(path);
            return null;
        }
        catch (Exception ex) when (IsExpectedBackupException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return $"Temporary import storage could not be removed: '{path}'.";
        }
    }

    private void TryCleanupFinishedImportFolders()
    {
        if (!Directory.Exists(_backupDirectory))
            return;

        try
        {
            FileSystemLinkValidator.ValidateDirectory(_backupDirectory);
            foreach (var path in Directory.EnumerateDirectories(
                         _backupDirectory,
                         RestoredImportDirectoryPrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteImportDirectory(path);
            }

            FileSystemLinkValidator.ValidatePath(GetRestoreRequestPath());
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
                         PendingImportDirectoryPrefix + "*",
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
            FileSystemLinkValidator.ValidatePath(GetRestoreRequestPath());
            var request = JsonFileStore.Read<FullBackupRestoreRequest>(
                requestPath,
                BackupFormat.JsonOptions);
            if (request.Status == JsonFileReadStatus.Missing ||
                !request.Success ||
                request.Value?.ImportId == importId)
            {
                FileSystemLinkValidator.ValidatePath(requestPath);
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
            (!name.StartsWith(ImportingDirectoryPrefix, StringComparison.OrdinalIgnoreCase) &&
             !name.StartsWith(PendingImportDirectoryPrefix, StringComparison.OrdinalIgnoreCase) &&
             !name.StartsWith(RestoredImportDirectoryPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("The import folder is outside Noted's backup storage.");
        }

        FileSystemLinkValidator.ValidateTree(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    private sealed record ImportedNoteHistoryEntry(
        int SchemaVersion,
        string NotePath,
        string Content,
        DateTime SavedUtc);
}
