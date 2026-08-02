using System.IO;
using System.Text.Json;

namespace Noted.Services;

internal sealed record PersistenceFileIssue(
    string FilePath,
    string Message);

internal sealed record JsonCollectionFileLoadResult<TItem>(
    IReadOnlyList<TItem>? Items,
    IReadOnlyList<PersistenceFileIssue> Issues);

internal sealed class JsonCollectionFileStore<TItem>
    where TItem : class
{
    private readonly string _primaryFilePath;
    private readonly string _backupFilePath;
    private readonly string _contentName;
    private bool _preserveBackupOnNextSave;
    private bool _writesBlocked;

    internal JsonCollectionFileStore(string filePath, string contentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentName);
        _primaryFilePath = Path.GetFullPath(filePath);
        _backupFilePath = $"{_primaryFilePath}.bak";
        _contentName = contentName;
    }

    internal string PrimaryFilePath => _primaryFilePath;

    internal JsonCollectionFileLoadResult<TItem> Load()
    {
        var issues = new List<PersistenceFileIssue>();
        var primary = ReadItems(_primaryFilePath);
        if (primary.Items is not null)
            return new JsonCollectionFileLoadResult<TItem>(primary.Items, issues);

        var backup = ReadItems(_backupFilePath);
        if (backup.Items is not null)
        {
            var preservedPath = PreserveCorruptFile(_primaryFilePath, primary.Status, issues);
            if (primary.Status == JsonFileReadStatus.Unavailable ||
                (primary.Status == JsonFileReadStatus.Corrupt && preservedPath is null))
            {
                _preserveBackupOnNextSave = true;
            }

            issues.Add(new PersistenceFileIssue(
                _primaryFilePath,
                primary.Status == JsonFileReadStatus.Missing
                    ? $"{_contentName} was restored from its backup because the primary file was missing."
                    : $"{_contentName} was restored from its backup because the primary file could not be loaded."));
            return new JsonCollectionFileLoadResult<TItem>(backup.Items, issues);
        }

        var primaryPreserved = PreserveCorruptFile(_primaryFilePath, primary.Status, issues);
        var backupPreserved = PreserveCorruptFile(_backupFilePath, backup.Status, issues);
        _writesBlocked = primary.Status == JsonFileReadStatus.Unavailable ||
            backup.Status == JsonFileReadStatus.Unavailable ||
            (primary.Status == JsonFileReadStatus.Corrupt && primaryPreserved is null) ||
            (backup.Status == JsonFileReadStatus.Corrupt && backupPreserved is null);

        var failure = primary.Status != JsonFileReadStatus.Missing
            ? primary
            : backup;
        if (failure.Status != JsonFileReadStatus.Missing)
        {
            issues.Add(new PersistenceFileIssue(
                failure.Path,
                $"{_contentName} could not be restored: {failure.Error ?? "the content file is invalid."}"));
        }

        return new JsonCollectionFileLoadResult<TItem>(null, issues);
    }

    internal void Write(IReadOnlyList<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_writesBlocked)
        {
            throw new IOException(
                $"{_contentName} cannot be saved because an existing content file could not be read or preserved. Resolve the reported file error and restart Noted before retrying.");
        }

        JsonFileStore.Write(
            _primaryFilePath,
            items.ToList(),
            _preserveBackupOnNextSave ? null : _backupFilePath);
        _preserveBackupOnNextSave = false;
    }

    internal void WriteAndVerify(IReadOnlyList<TItem> items)
    {
        Write(items);
        var verification = ReadItems(_primaryFilePath);
        if (verification.Items is null)
        {
            throw new IOException(
                $"{_contentName} was written but could not be verified: {verification.Error ?? "the new file could not be read."}");
        }
    }

    private static ItemReadAttempt ReadItems(string path)
    {
        var result = JsonFileStore.Read<List<TItem>>(path);
        if (!result.Success)
            return new ItemReadAttempt(path, result.Status, null, result.Error?.Message);
        if (result.Value!.Any(item => item is null))
        {
            return new ItemReadAttempt(
                path,
                JsonFileReadStatus.Corrupt,
                null,
                "The content file contains an empty item.");
        }

        return new ItemReadAttempt(path, JsonFileReadStatus.Success, result.Value, null);
    }

    private static string? PreserveCorruptFile(
        string path,
        JsonFileReadStatus status,
        ICollection<PersistenceFileIssue> issues)
    {
        if (status != JsonFileReadStatus.Corrupt || !File.Exists(path))
            return null;

        var preservedPath =
            $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, preservedPath);
            issues.Add(new PersistenceFileIssue(
                path,
                $"The corrupt content file was preserved as '{Path.GetFileName(preservedPath)}'."));
            return preservedPath;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new PersistenceFileIssue(
                path,
                $"The corrupt content file could not be preserved under a new name: {ex.Message}"));
            return null;
        }
    }

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            JsonException or
            ArgumentException or
            NotSupportedException;
    }

    private sealed record ItemReadAttempt(
        string Path,
        JsonFileReadStatus Status,
        IReadOnlyList<TItem>? Items,
        string? Error);
}
