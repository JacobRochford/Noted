using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Noted.Models;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed class BackupContentCatalog
{
    private readonly string _appDataDirectory;
    private readonly string _backupDirectory;
    private string _notesDirectory;

    internal BackupContentCatalog(
        string appDataDirectory,
        string notesDirectory,
        string backupDirectory)
    {
        _appDataDirectory = Path.GetFullPath(appDataDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
        _backupDirectory = Path.GetFullPath(backupDirectory);
    }

    internal void UpdateNotesDirectory(string notesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesDirectory);
        _notesDirectory = Path.GetFullPath(notesDirectory);
    }

    internal IReadOnlyList<BackupSourceEntry> Collect() => Collect(out _, out _);

    internal IReadOnlyList<BackupSourceEntry> Collect(
        out IReadOnlySet<string> selectedNotePaths,
        out int externalDocumentCount,
        bool includeNotes = true)
    {
        FileSystemLinkValidator.ValidateDirectory(_appDataDirectory);
        FileSystemLinkValidator.ValidateWithinRoot(_notesDirectory, _notesDirectory);
        if (!Directory.Exists(_notesDirectory))
            throw new DirectoryNotFoundException($"The notes folder '{_notesDirectory}' could not be found.");

        var sources = new List<BackupSourceEntry>();
        NoteEditorSession? session = null;
        foreach (var definition in BackupContentDefinitions.FixedFiles)
        {
            var capturedSession = AddAppFile(sources, definition);
            if (capturedSession is not null)
                session = capturedSession;
        }
        AddNoteRecoveryFiles(sources);
        AddNoteHistoryFiles(sources);
        AddDeletedNotes(sources);
        selectedNotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        externalDocumentCount = 0;
        if (includeNotes)
            AddNotes(sources, session, out selectedNotePaths, out externalDocumentCount);

        var duplicate = sources
            .GroupBy(source => source.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new IOException($"Multiple source files map to '{duplicate.Key}'.");

        return sources
            .OrderBy(source => source.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private NoteEditorSession? AddAppFile(ICollection<BackupSourceEntry> sources, FixedBackupFile definition)
    {
        var sourcePath = Path.Combine(_appDataDirectory, definition.RelativePath);
        FileSystemLinkValidator.ValidatePath(sourcePath);
        if (definition.Content == FixedBackupContent.JotCapture && FileSystemLinkValidator.GetAttributes(sourcePath) is null)
        {
            sourcePath += ".bak";
            FileSystemLinkValidator.ValidatePath(sourcePath);
        }
        if (FileSystemLinkValidator.GetAttributes(sourcePath) is null)
            return null;

        NoteEditorSession? session = null;
        sources.Add(CreateSourceEntry(
            definition.LogicalPath,
            sourcePath,
            bytes =>
            {
                if (definition.Content != FixedBackupContent.EditorSession)
                    return ValidateFixedFile(definition.Content, bytes);
                session = ValidateEditorSession(bytes);
                return new BackupContentDetails(null, null);
            }));
        return session;
    }

    internal static BackupContentDetails ValidateFixedFile(FixedBackupContent content, byte[] bytes)
    {
        switch (content)
        {
            case FixedBackupContent.Settings:
                return ValidateSettings(bytes);
            case FixedBackupContent.Checklist:
                return new BackupContentDetails(ValidateCollection<List<ChecklistItemState>>(bytes).Count, null);
            case FixedBackupContent.ChecklistTabs:
                return new BackupContentDetails(ValidateCollection<List<ChecklistTabState>>(bytes).Count, null);
            case FixedBackupContent.Dictionary:
                return new BackupContentDetails(ValidateCollection<List<DictionaryItemState>>(bytes).Count, null);
            case FixedBackupContent.Jots:
                return new BackupContentDetails(ValidateCollection<List<JotItemState>>(bytes).Count, null);
            case FixedBackupContent.JotCapture:
                var draft = JotCaptureDraftService.ValidateBackupDraft(bytes);
                return new BackupContentDetails(null, draft?.Text.Length ?? 0);
            case FixedBackupContent.Scratchpad:
                return new BackupContentDetails(null, bytes.LongLength);
            case FixedBackupContent.EditorSession:
                _ = ValidateEditorSession(bytes);
                return new BackupContentDetails(null, null);
            default:
                throw new ArgumentOutOfRangeException(nameof(content));
        }
    }

    private void AddNoteRecoveryFiles(ICollection<BackupSourceEntry> sources)
    {
        var recoveryDirectory = Path.Combine(_appDataDirectory, PersistencePaths.RecoveryDirectory);
        FileSystemLinkValidator.ValidateDirectory(recoveryDirectory);
        if (!Directory.Exists(recoveryDirectory))
            return;

        ThrowIfDirectoryIsLink(recoveryDirectory);
        foreach (var path in Directory.EnumerateFiles(
                     recoveryDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            AddRecoveryFile(sources, path, bytes => ValidateNoteDraft(bytes, path));
        }

        var miniPadDirectory = MiniPadStorage.GetRecoveryDirectory(_appDataDirectory);
        FileSystemLinkValidator.ValidateDirectory(miniPadDirectory);
        if (Directory.Exists(miniPadDirectory))
            ThrowIfDirectoryIsLink(miniPadDirectory);

        var settingsPath = Path.Combine(_appDataDirectory, PersistencePaths.SettingsFile);
        FileSystemLinkValidator.ValidatePath(settingsPath);
        var settings = File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<AppSettingsDocument>(File.ReadAllBytes(settingsPath))
            : null;
        IEnumerable<Guid> miniPadIds = settings?.MiniPadWindows?.Keys.AsEnumerable()
            ?? [MiniPadRecoveryService.LegacyDraftId];
        foreach (var id in miniPadIds.Where(id => id != Guid.Empty))
        {
            var miniPadPath = Path.Combine(miniPadDirectory, $"{id:N}.json");
            if (File.Exists(miniPadPath))
                AddRecoveryFile(sources, miniPadPath, bytes => ValidateMiniPadDraft(bytes, miniPadPath));
        }
    }

    private void AddRecoveryFile(
        ICollection<BackupSourceEntry> sources,
        string path,
        Func<byte[], BackupContentDetails> validator)
    {
        FileSystemLinkValidator.ValidatePath(path);

        var relativePath = Path.GetRelativePath(_appDataDirectory, path);
        sources.Add(CreateSourceEntry(
            ToLogicalPath(PersistencePaths.AppBackupRoot, relativePath),
            path,
            validator));
    }

    private static void ThrowIfDirectoryIsLink(string path) =>
        FileSystemLinkValidator.ValidateDirectory(path);

    private void AddNoteHistoryFiles(ICollection<BackupSourceEntry> sources)
    {
        var historyDirectory = Path.Combine(_appDataDirectory, PersistencePaths.NoteHistoryDirectory);
        FileSystemLinkValidator.ValidateDirectory(historyDirectory);
        if (!Directory.Exists(historyDirectory))
            return;

        foreach (var path in EnumerateFilesWithoutLinks(historyDirectory))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(_appDataDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath(PersistencePaths.AppBackupRoot, relativePath),
                path,
                ValidateNoteHistoryEntry));
        }
    }

    private void AddDeletedNotes(ICollection<BackupSourceEntry> sources)
    {
        var deletedNotesDirectory = Path.Combine(_appDataDirectory, PersistencePaths.DeletedNotesDirectory);
        FileSystemLinkValidator.ValidateDirectory(deletedNotesDirectory);
        if (!Directory.Exists(deletedNotesDirectory))
            return;

        foreach (var path in EnumerateFilesWithoutLinks(deletedNotesDirectory))
        {
            if (!NoteFileExtensions.IsSupported(path))
                continue;

            var relativePath = Path.GetRelativePath(_appDataDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath(PersistencePaths.AppBackupRoot, relativePath),
                path,
                bytes => new BackupContentDetails(null, bytes.LongLength)));
        }
    }

    private void AddNotes(
        ICollection<BackupSourceEntry> sources,
        NoteEditorSession? session,
        out IReadOnlySet<string> selectedNotePaths,
        out int externalDocumentCount)
    {
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var externalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in session?.Tabs ?? [])
        {
            if (string.IsNullOrWhiteSpace(tab.FilePath) || !Path.IsPathFullyQualified(tab.FilePath) ||
                !NoteFileExtensions.IsSupported(tab.FilePath))
                throw new InvalidDataException("The editor session contains an invalid saved-document path.");
            var path = Path.GetFullPath(tab.FilePath);
            if (!IsPathWithin(path, _notesDirectory))
            {
                externalPaths.Add(path);
                continue;
            }
            selected[ToLogicalPath(PersistencePaths.NotesBackupRoot, Path.GetRelativePath(_notesDirectory, path))] = path;
        }

        // establish selection before reading files so a missing open document cannot become an exclusion.
        selectedNotePaths = selected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        externalDocumentCount = externalPaths.Count;
        var archiveDirectory = Path.Combine(_notesDirectory, PersistencePaths.ArchivedNotesDirectory);
        FileSystemLinkValidator.ValidateWithinRoot(_notesDirectory, archiveDirectory);
        if (DirectoryExistsOrThrow(archiveDirectory))
        {
            foreach (var path in EnumerateFilesWithoutLinks(archiveDirectory, [_backupDirectory], allowRootLinks: true))
            {
                if (NoteFileExtensions.IsSupported(path))
                    selected[ToLogicalPath(PersistencePaths.NotesBackupRoot, Path.GetRelativePath(_notesDirectory, path))] = path;
            }
        }

        var backupRoot = FileSystemLinkValidator.ResolveDirectory(_backupDirectory);
        foreach (var (logicalPath, path) in selected)
        {
            var readPath = FileSystemLinkValidator.ValidateWithinRoot(_notesDirectory, path);
            if (PathsEqual(readPath, backupRoot) || IsPathWithin(readPath, backupRoot))
                throw new IOException("An open document is inside Noted's backup storage.");
            sources.Add(CreateSourceEntry(
                logicalPath,
                path,
                bytes => new BackupContentDetails(null, bytes.LongLength)));
        }
    }

    private static bool DirectoryExistsOrThrow(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
                throw new IOException($"The archive path '{path}' is not a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private BackupSourceEntry CreateSourceEntry(
        string logicalPath,
        string sourcePath,
        Func<byte[], BackupContentDetails> validator,
        string? notesDirectory = null)
    {
        var readPath = logicalPath.StartsWith(PersistencePaths.NotesBackupPrefix, StringComparison.OrdinalIgnoreCase)
            ? FileSystemLinkValidator.ValidateWithinRoot(notesDirectory ?? _notesDirectory, sourcePath)
            : sourcePath;
        var bytes = ReadFileBytes(readPath);
        var details = validator(bytes);
        return new BackupSourceEntry(
            logicalPath,
            Path.GetFullPath(sourcePath),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            details.ItemCount,
            details.ContentLength);
    }

    internal BackupSourceEntry? CaptureRestoreDestination(string logicalPath, string destination, string notesRoot)
    {
        try
        {
            if ((File.GetAttributes(destination) & FileAttributes.Directory) != 0)
                throw new IOException($"The restore destination '{destination}' is a directory.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        var definition = BackupContentDefinitions.Find(logicalPath);
        return CreateSourceEntry(logicalPath, destination,
            bytes => definition is null
                ? new BackupContentDetails(null, bytes.LongLength)
                : ValidateFixedFile(definition.Content, bytes), notesRoot);
    }

    internal static BackupContentDetails ValidateSettings(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The Settings file does not contain a JSON object.");

        if (document.RootElement.TryGetProperty("SchemaVersion", out var schemaElement))
        {
            if (schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion < 0 ||
                schemaVersion > SupportedSettingsSchemaVersion)
            {
                throw new JsonException("The Settings schema is not supported.");
            }
        }

        if (document.RootElement.TryGetProperty("Revision", out var revisionElement) &&
            (revisionElement.ValueKind != JsonValueKind.Number ||
             !revisionElement.TryGetInt64(out var revision) || revision < 0))
        {
            throw new JsonException("The Settings revision is invalid.");
        }

        return new BackupContentDetails(null, null);
    }

    internal static T ValidateCollection<T>(byte[] bytes)
        where T : class
    {
        var value = JsonSerializer.Deserialize<T>(bytes)
            ?? throw new JsonException("The JSON collection contains no value.");
        if (value is System.Collections.IEnumerable items &&
            items.Cast<object?>().Any(item => item is null))
        {
            throw new JsonException("The JSON collection contains an empty item.");
        }

        return value;
    }

    internal static NoteEditorSession ValidateEditorSession(byte[] bytes)
    {
        var session = JsonSerializer.Deserialize<NoteEditorSession>(bytes)
            ?? throw new JsonException("The editor session contains no value.");
        NoteEditorSessionService.ValidateSession(session);
        return session;
    }

    internal static BackupContentDetails ValidateMiniPadDraft(byte[] bytes, string path)
    {
        var draft = JsonSerializer.Deserialize<MiniPadRecoveryDraft>(bytes)
            ?? throw new JsonException("The MiniPad recovery file contains no value.");
        if (draft.Id == Guid.Empty ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                draft.Id.ToString("N"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("The MiniPad recovery ID does not match its file name.");
        }

        return new BackupContentDetails(null, draft.Content?.Length ?? 0);
    }

    internal static BackupContentDetails ValidateNoteDraft(byte[] bytes, string path)
    {
        var draft = JsonSerializer.Deserialize<NoteRecoveryDraft>(bytes)
            ?? throw new JsonException("The note recovery file contains no value.");
        if (string.IsNullOrWhiteSpace(draft.FilePath) ||
            !NoteFileExtensions.IsSupported(draft.FilePath))
        {
            throw new JsonException("The note recovery file does not contain a supported note path.");
        }

        if (!Path.GetFileName(path).Equals(PersistencePaths.LegacyEditorDraftFile, StringComparison.OrdinalIgnoreCase))
        {
            var expectedName = NotePathIdentity.GetHash(NormalizeBackupDataPath(draft.FilePath)) + ".json";
            if (!Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("The note path does not match the recovery file name.");
        }

        return new BackupContentDetails(null, draft.Content?.Length ?? 0);
    }

    internal static BackupContentDetails ValidateNoteHistoryEntry(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("SchemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaVersion) ||
            schemaVersion != 1 ||
            !root.TryGetProperty("NotePath", out var notePath) ||
            notePath.ValueKind != JsonValueKind.String ||
            !NoteFileExtensions.IsSupported(notePath.GetString() ?? string.Empty) ||
            !root.TryGetProperty("Content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("SavedUtc", out var savedUtc) ||
            savedUtc.ValueKind != JsonValueKind.String ||
            !savedUtc.TryGetDateTime(out _))
        {
            throw new JsonException("The saved-note history entry is invalid.");
        }

        return new BackupContentDetails(null, content.GetString()?.Length ?? 0);
    }

    internal static IEnumerable<string> EnumerateFilesWithoutLinks(
        string rootDirectory,
        IReadOnlyCollection<string>? excludedDirectories = null,
        bool allowRootLinks = false)
    {
        var exclusions = (excludedDirectories ?? [])
            .Select(path => allowRootLinks ? FileSystemLinkValidator.ResolveDirectory(path) : Path.GetFullPath(path))
            .ToList();
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (allowRootLinks)
            FileSystemLinkValidator.ValidateWithinRoot(fullRoot, fullRoot);
        else
            FileSystemLinkValidator.ValidateDirectory(fullRoot);
        var exclusionRoot = allowRootLinks ? FileSystemLinkValidator.ResolveDirectory(fullRoot) : fullRoot;
        if (exclusions.Any(exclusion => PathsEqual(exclusionRoot, exclusion) || IsPathWithin(exclusionRoot, exclusion)))
            yield break;

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (allowRootLinks)
                FileSystemLinkValidator.ValidateWithinRoot(fullRoot, directory);
            else
                FileSystemLinkValidator.ValidateDirectory(directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (allowRootLinks)
                    FileSystemLinkValidator.ValidateWithinRoot(fullRoot, path);
                else
                    FileSystemLinkValidator.ValidatePath(path);
                yield return path;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (allowRootLinks)
                    FileSystemLinkValidator.ValidateWithinRoot(fullRoot, child);
                else
                    FileSystemLinkValidator.ValidatePath(child);
                var exclusionChild = allowRootLinks ? FileSystemLinkValidator.ValidateWithinRoot(fullRoot, child) : child;
                if (exclusions.Any(exclusion => PathsEqual(exclusionChild, exclusion) || IsPathWithin(exclusionChild, exclusion)))
                    continue;
                pending.Push(child);
            }
        }
    }

    private static byte[] ReadFileBytes(string path)
    {
        FileSystemLinkValidator.ValidatePath(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
            throw new IOException($"'{path}' is too large for the current backup implementation.");

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        var directoryWithSeparator = Path.EndsInDirectorySeparator(fullDirectory)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

internal sealed record BackupSourceEntry(
    string LogicalPath,
    string SourcePath,
    long Length,
    string Sha256,
    int? ItemCount,
    long? ContentLength)
{
    internal FullBackupEntry ToManifestEntry() =>
        new(LogicalPath, SourcePath, Length, Sha256, ItemCount, ContentLength);
}

internal sealed record BackupContentDetails(int? ItemCount, long? ContentLength);
