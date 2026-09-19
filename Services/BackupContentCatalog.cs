using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Noted.Models;
using static Noted.Services.BackupFormat;

namespace Noted.Services;

internal sealed class BackupContentCatalog
{
    internal static readonly Guid s_miniPadDraftId =
        new("A6CB4208-79C0-4C08-9D07-9CDF33F31337");

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

    internal IReadOnlyList<BackupSourceEntry> Collect()
    {
        if (!Directory.Exists(_notesDirectory))
            throw new DirectoryNotFoundException($"The notes folder '{_notesDirectory}' could not be found.");

        var sources = new List<BackupSourceEntry>();
        AddAppFile(sources, "settings.json", ValidateSettings);
        AddAppFile<List<ChecklistItemState>>(
            sources,
            "checklist.json",
            ValidateCollection<List<ChecklistItemState>>,
            items => items.Count);
        AddAppFile<List<ChecklistTabState>>(
            sources,
            "checklist-tabs.json",
            ValidateCollection<List<ChecklistTabState>>,
            items => items.Count);
        AddAppFile<List<DictionaryItemState>>(
            sources,
            "dictionary.json",
            ValidateCollection<List<DictionaryItemState>>,
            items => items.Count);
        AddAppTextFile(sources, "scratchpad.rtf");
        AddAppFile<NoteEditorSession>(
            sources,
            Path.Combine("session", "editor-workspace.json"),
            ValidateEditorSession);
        AddNoteRecoveryFiles(sources);
        AddNoteHistoryFiles(sources);
        AddDeletedNotes(sources);
        AddNotes(sources);

        var duplicate = sources
            .GroupBy(source => source.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new IOException($"Multiple source files map to '{duplicate.Key}'.");

        return sources
            .OrderBy(source => source.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddAppFile(
        ICollection<BackupSourceEntry> sources,
        string relativePath,
        Func<byte[], BackupContentDetails> validator)
    {
        var sourcePath = Path.Combine(_appDataDirectory, relativePath);
        if (!File.Exists(sourcePath))
            return;

        sources.Add(CreateSourceEntry(
            ToLogicalPath("app", relativePath),
            sourcePath,
            validator));
    }

    private void AddAppFile<T>(
        ICollection<BackupSourceEntry> sources,
        string relativePath,
        Func<byte[], T> validator,
        Func<T, int>? itemCount = null)
    {
        AddAppFile(
            sources,
            relativePath,
            bytes =>
            {
                var value = validator(bytes);
                return new BackupContentDetails(itemCount?.Invoke(value), null);
            });
    }

    private void AddAppTextFile(ICollection<BackupSourceEntry> sources, string relativePath)
    {
        AddAppFile(
            sources,
            relativePath,
            bytes => new BackupContentDetails(null, bytes.LongLength));
    }

    private void AddNoteRecoveryFiles(ICollection<BackupSourceEntry> sources)
    {
        var recoveryDirectory = Path.Combine(_appDataDirectory, "recovery");
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

        var miniPadDirectory = Path.Combine(recoveryDirectory, "micro-scratchpads");
        if (Directory.Exists(miniPadDirectory))
            ThrowIfDirectoryIsLink(miniPadDirectory);

        var miniPadPath = Path.Combine(miniPadDirectory, $"{s_miniPadDraftId:N}.json");
        if (File.Exists(miniPadPath))
            AddRecoveryFile(sources, miniPadPath, bytes => ValidateMiniPadDraft(bytes, miniPadPath));
    }

    private void AddRecoveryFile(
        ICollection<BackupSourceEntry> sources,
        string path,
        Func<byte[], BackupContentDetails> validator)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The recovery file '{path}' is a filesystem link and cannot be included safely.");

        var relativePath = Path.GetRelativePath(_appDataDirectory, path);
        sources.Add(CreateSourceEntry(
            ToLogicalPath("app", relativePath),
            path,
            validator));
    }

    private static void ThrowIfDirectoryIsLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The recovery folder '{path}' is a filesystem link and cannot be included safely.");
    }

    private void AddNoteHistoryFiles(ICollection<BackupSourceEntry> sources)
    {
        var historyDirectory = Path.Combine(_appDataDirectory, "note-history");
        if (!Directory.Exists(historyDirectory))
            return;

        foreach (var path in EnumerateFilesWithoutLinks(historyDirectory))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(_appDataDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath("app", relativePath),
                path,
                ValidateNoteHistoryEntry));
        }
    }

    private void AddDeletedNotes(ICollection<BackupSourceEntry> sources)
    {
        var deletedNotesDirectory = Path.Combine(_appDataDirectory, "DeletedNotes");
        if (!Directory.Exists(deletedNotesDirectory))
            return;

        foreach (var path in EnumerateFilesWithoutLinks(deletedNotesDirectory))
        {
            if (!NoteFileExtensions.IsSupported(path))
                continue;

            var relativePath = Path.GetRelativePath(_appDataDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath("app", relativePath),
                path,
                bytes => new BackupContentDetails(null, bytes.LongLength)));
        }
    }

    private void AddNotes(ICollection<BackupSourceEntry> sources)
    {
        foreach (var path in EnumerateFilesWithoutLinks(_notesDirectory, [_backupDirectory]))
        {
            if (!NoteFileExtensions.IsSupported(path))
                continue;

            var relativePath = Path.GetRelativePath(_notesDirectory, path);
            sources.Add(CreateSourceEntry(
                ToLogicalPath("notes", relativePath),
                path,
                bytes => new BackupContentDetails(null, bytes.LongLength)));
        }
    }

    private static BackupSourceEntry CreateSourceEntry(
        string logicalPath,
        string sourcePath,
        Func<byte[], BackupContentDetails> validator)
    {
        var bytes = ReadFileBytes(sourcePath);
        var details = validator(bytes);
        return new BackupSourceEntry(
            logicalPath,
            Path.GetFullPath(sourcePath),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            details.ItemCount,
            details.ContentLength);
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
        if (session.Tabs is null)
            throw new JsonException("The editor session does not contain a tab list.");
        return session;
    }

    internal static BackupContentDetails ValidateMiniPadDraft(byte[] bytes, string path)
    {
        var draft = JsonSerializer.Deserialize<MiniPadRecoveryDraft>(bytes)
            ?? throw new JsonException("The MiniPad recovery file contains no value.");
        if (draft.Id != s_miniPadDraftId ||
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

        if (!Path.GetFileName(path).Equals("editor-draft.json", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedPath = NormalizeBackupDataPath(draft.FilePath).ToUpperInvariant();
            var expectedName = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath))) + ".json";
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
        IReadOnlyCollection<string>? excludedDirectories = null)
    {
        var exclusions = (excludedDirectories ?? [])
            .Select(Path.GetFullPath)
            .ToList();
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (exclusions.Any(exclusion => PathsEqual(fullRoot, exclusion) || IsPathWithin(fullRoot, exclusion)))
            yield break;

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The source file '{path}' is a filesystem link and cannot be included safely.");
                yield return path;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The source folder '{child}' is a filesystem link and cannot be included safely.");
                if (exclusions.Any(exclusion => PathsEqual(child, exclusion) || IsPathWithin(child, exclusion)))
                    continue;
                pending.Push(child);
            }
        }
    }

    private static byte[] ReadFileBytes(string path)
    {
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
