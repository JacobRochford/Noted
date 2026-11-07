using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noted.Services;

internal sealed record NoteDraftState(string InitialContent, string? CleanupContentHash = null,
    bool CleanupCommitted = false, Guid CleanupToken = default, string? SavedFilePath = null,
    string? SavedFileHash = null, bool Discarded = false);

internal sealed class NoteDraftCleanupBlockedException(string message, Exception? inner = null)
    : IOException(message, inner)
{
}

internal sealed class NoteDraftStorage(string appDataDirectory)
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(appDataDirectory), PersistencePaths.NoteDraftsDirectory);

    internal bool ContainsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (IsWithinDirectory(fullPath, DirectoryPath))
            return true;
        var parent = Path.GetDirectoryName(fullPath);
        return parent is not null && IsWithinDirectory(
            Path.Combine(FileSystemLinkValidator.ResolveDirectory(parent), Path.GetFileName(fullPath)),
            Path.Combine(FileSystemLinkValidator.ResolveDirectory(Path.GetDirectoryName(DirectoryPath)!), Path.GetFileName(DirectoryPath)));
    }

    internal bool IsDraftPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) || !ContainsPath(path))
            return false;
        FileSystemLinkValidator.ValidatePath(path);
        return true;
    }

    internal static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static bool IsBackupPath(string path) =>
        path.Replace('\\', '/').StartsWith(PersistencePaths.NoteDraftsBackupPrefix, StringComparison.OrdinalIgnoreCase);

    internal static string GetMetadataPath(string path) => path + ".json";

    // a new draft must never inherit an older path's pending cleanup intent.
    internal static string CreateFileName(DateTime timestamp) =>
        $"{timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.txt";

    internal void Create(string path, string initialContent)
    {
        ValidateDraftPath(path);
        FileWriter.WriteAllBytes(path, TextFileCodec.Encode(initialContent, Models.TextFileEncoding.Utf8), createOnly: true);
        var state = new NoteDraftState(initialContent);
        FileWriter.WriteAllBytes(GetMetadataPath(path), JsonSerializer.SerializeToUtf8Bytes(state), createOnly: true);
        if (TextFileCodec.Read(path).Content != initialContent || Read(path) != state)
            throw new FileVerificationException("The Quick Note backing file could not be verified.");
    }

    internal NoteDraftState? Read(string path)
    {
        ValidateDraftPath(path);
        var metadata = GetMetadataPath(path);
        var primary = ReadStateFile(metadata);
        if (primary.Success)
            return ValidateState(primary.Value!);
        var backup = ReadStateFile(metadata + ".bak");
        if (backup.Success && primary.Status != JsonFileReadStatus.Unavailable)
        {
            var state = ValidateState(backup.Value!);
            var preserved = JsonFileStore.PreserveCorruptFile(metadata, primary.Status);
            if (preserved.Error is not null)
                throw new IOException("Quick Note metadata could not be preserved.", preserved.Error);
            JsonFileStore.Write(metadata, state);
            return state;
        }
        if (primary.Status == JsonFileReadStatus.Missing && backup.Status == JsonFileReadStatus.Missing)
            return null;
        throw new IOException($"Quick Note metadata could not be read: '{metadata}'.", primary.Error ?? backup.Error);
    }

    internal void Write(string path, NoteDraftState state)
    {
        ValidateDraftPath(path);
        ValidateState(state);
        var existing = Read(path);
        var metadata = GetMetadataPath(path);
        if (existing is null)
        {
            FileWriter.WriteAllBytes(metadata, JsonSerializer.SerializeToUtf8Bytes(state), createOnly: true);
            if (Read(path) != state)
                throw new FileVerificationException("The Quick Note metadata could not be verified.");
            return;
        }
        var result = JsonFileStore.Write(metadata, state, metadata + ".bak");
        if (result.Warning is not null)
            throw new IOException(result.Warning);
    }

    internal IReadOnlyList<string> GetDraftPaths()
    {
        FileSystemLinkValidator.ValidateDirectory(DirectoryPath);
        if (!Directory.Exists(DirectoryPath))
            return [];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(DirectoryPath))
        {
            var path = file.EndsWith(".txt.json.bak", StringComparison.OrdinalIgnoreCase) ? file[..^9]
                : file.EndsWith(".txt.json", StringComparison.OrdinalIgnoreCase) ? file[..^5] : file;
            if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                paths.Add(path);
        }
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal Guid PrepareCleanup(string path, string content, string? initialContent = null,
        string? destination = null, Models.TextFileEncoding encoding = Models.TextFileEncoding.Utf8)
    {
        var state = Read(path);
        if (state is null)
        {
            if (initialContent is null)
                throw new IOException("The Quick Note's initial content is unknown; its files were kept.");
            state = new NoteDraftState(initialContent);
        }
        var token = Guid.NewGuid();
        Write(path, state with
        {
            CleanupContentHash = HashContent(content), CleanupCommitted = false, CleanupToken = token,
            SavedFilePath = destination is null ? null : Path.GetFullPath(destination),
            SavedFileHash = destination is null ? null : Convert.ToHexString(SHA256.HashData(TextFileCodec.Encode(content, encoding))),
            Discarded = destination is null
        });
        return token;
    }

    internal void CommitCleanup(string path)
    {
        var state = Read(path) ?? throw new IOException("Quick Note cleanup metadata is missing.");
        Write(path, state with { CleanupCommitted = true });
    }

    internal void Cleanup(string path, INoteRecoveryService recovery)
    {
        var state = Read(path) ?? throw new IOException("Quick Note cleanup metadata is missing.");
        if (!state.CleanupCommitted)
            throw new IOException("The Quick Note session transition has not been committed.");
        var draft = recovery.LoadDraft(path);
        if (new NoteRecoveryService(Path.GetDirectoryName(DirectoryPath)!).HasUnverifiedDraftFiles(path))
            throw new NoteDraftCleanupBlockedException("Unverified Quick Note recovery files were kept for review.");
        if (draft.Draft is { } saved && HashContent(saved.Content) != state.CleanupContentHash ||
            draft.Draft is null && draft.Issues.Count > 0)
            throw new NoteDraftCleanupBlockedException("Quick Note recovery data changed or could not be verified; it was kept.");
        using var destination = ProtectSavedFile(state);
        try
        {
            if (File.Exists(path) && TextFileCodec.Read(path).Content != state.InitialContent)
                throw new NoteDraftCleanupBlockedException("The Quick Note backing file changed; it was kept for recovery.");
        }
        catch (Exception ex) when (ex is not NoteDraftCleanupBlockedException && FileSystemErrors.IsExpected(ex))
        {
            throw new NoteDraftCleanupBlockedException("The Quick Note backing file could not be verified; it was kept.", ex);
        }
        NoteContentService.DeleteInitialFile(path, state.InitialContent);
        recovery.DeleteDraft(path);
        var metadata = GetMetadataPath(path);
        FileSystemLinkValidator.ValidatePath(metadata + ".bak");
        FileWriter.DeleteIfExists(metadata + ".bak");
        FileSystemLinkValidator.ValidatePath(metadata);
        FileWriter.DeleteIfExists(metadata);
    }

    internal static NoteDraftState ValidateState(NoteDraftState state)
    {
        if (state.InitialContent is null || state.CleanupCommitted && state.CleanupContentHash is null ||
            state.CleanupContentHash is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new IOException("The Quick Note metadata is invalid.");
        if (state.SavedFilePath is not null &&
            (!Path.IsPathFullyQualified(state.SavedFilePath) || !NoteFileExtensions.IsSupported(state.SavedFilePath) ||
             state.SavedFileHash is null || state.SavedFileHash.Length != 64 || !state.SavedFileHash.All(Uri.IsHexDigit)) ||
            state.Discarded && (state.SavedFilePath is not null || state.SavedFileHash is not null))
            throw new IOException("The Quick Note save destination is invalid.");
        if (state.CleanupToken != Guid.Empty && state.CleanupContentHash is not null &&
            !state.Discarded && (state.SavedFilePath is null || state.SavedFileHash is null))
            throw new IOException("The Quick Note save destination is missing.");
        if (state.SavedFilePath is not null)
        {
            try { _ = Path.GetFullPath(state.SavedFilePath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new IOException("The Quick Note save destination is invalid.", ex);
            }
        }
        return state;
    }

    internal void CancelCleanup(string path)
    {
        var state = Read(path) ?? throw new IOException("Quick Note metadata is missing.");
        Write(path, new NoteDraftState(state.InitialContent));
    }

    private FileStream? ProtectSavedFile(NoteDraftState state)
    {
        if (state.CleanupToken == Guid.Empty)
            throw new NoteDraftCleanupBlockedException("The older Quick Note cleanup record needs review; its content was kept.");
        if (state.Discarded)
            return null;
        if (state.SavedFilePath is null || state.SavedFileHash is null)
            throw new NoteDraftCleanupBlockedException("The Quick Note save destination cannot be verified; its content was kept.");
        FileStream? stream = null;
        try
        {
            if (ContainsPath(state.SavedFilePath))
                throw new IOException("The saved destination is inside managed draft storage.");
            FileSystemLinkValidator.ValidatePath(state.SavedFilePath);
            stream = new FileStream(state.SavedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(state.SavedFileHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The saved destination changed after the Quick Note was saved.");
            if (HashContent(TextFileCodec.Read(state.SavedFilePath).Content) != state.CleanupContentHash)
                throw new IOException("The saved destination does not contain the Quick Note's protected content.");
            return stream;
        }
        catch (Exception ex) when (FileSystemErrors.IsExpected(ex))
        {
            stream?.Dispose();
            throw new NoteDraftCleanupBlockedException("The saved destination is missing, changed, or unavailable; the Quick Note was kept for recovery.", ex);
        }
    }

    private static JsonFileReadResult<NoteDraftState> ReadStateFile(string path)
    {
        var result = JsonFileStore.Read<NoteDraftState>(path);
        if (!result.Success)
            return result;
        try
        {
            ValidateState(result.Value!);
            return result;
        }
        catch (IOException ex)
        {
            return new JsonFileReadResult<NoteDraftState>(JsonFileReadStatus.Corrupt, null, ex);
        }
    }

    private void ValidateDraftPath(string path)
    {
        if (!IsDraftPath(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The Quick Note is outside managed draft storage.");
    }

    private static string HashContent(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
