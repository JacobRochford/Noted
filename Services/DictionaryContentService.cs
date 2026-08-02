using System.IO;
using Noted.Models;

namespace Noted.Services;

public sealed class DictionaryContentService : IDictionaryContentService
{
    private readonly IAppSettingsService _settingsService;
    private readonly JsonCollectionFileStore<DictionaryItemState> _fileStore;
    private bool _legacyReadBlocksWrites;

    public DictionaryContentService(
        string storageDirectory,
        IAppSettingsService settingsService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
        _fileStore = new JsonCollectionFileStore<DictionaryItemState>(
            Path.Combine(Path.GetFullPath(storageDirectory), "dictionary.json"),
            "Dictionary content");
    }

    public DictionaryContentLoadResult LoadItems()
    {
        var fileResult = _fileStore.Load();
        var issues = fileResult.Issues
            .Select(issue => new DictionaryContentIssue(issue.FilePath, issue.Message))
            .ToList();

        if (!TryLoadLegacyItems(out var legacyItems, out var legacyError))
        {
            issues.Add(new DictionaryContentIssue(_fileStore.PrimaryFilePath, legacyError!));
            if (fileResult.Items is null)
                _legacyReadBlocksWrites = true;
            return new DictionaryContentLoadResult(fileResult.Items ?? [], issues);
        }

        if (fileResult.Items is not null)
        {
            TryClearLegacyItems(legacyItems, issues);
            return new DictionaryContentLoadResult(fileResult.Items, issues);
        }

        if (legacyItems is null)
            return new DictionaryContentLoadResult([], issues);

        try
        {
            _fileStore.WriteAndVerify(legacyItems);
            TryClearLegacyItems(legacyItems, issues);
        }
        catch (Exception ex) when (IsExpectedContentException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new DictionaryContentIssue(
                _fileStore.PrimaryFilePath,
                $"Dictionary content could not be migrated out of settings: {ex.Message}"));
        }

        return new DictionaryContentLoadResult(legacyItems, issues);
    }

    public DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_legacyReadBlocksWrites)
        {
            throw new IOException(
                "Dictionary content cannot be saved because the legacy settings copy could not be checked. Resolve the reported settings error and restart Noted before retrying.");
        }

        _fileStore.Write(items);
        try
        {
            var legacyItems = _settingsService.LoadLegacyDictionaryItems();
            if (legacyItems is not null)
                _settingsService.ClearLegacyDictionaryItems();
            return new DictionaryContentSaveResult(null);
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return new DictionaryContentSaveResult(
                $"Dictionary content was saved, but its old settings copy could not be removed: {ex.Message}");
        }
    }

    private bool TryLoadLegacyItems(
        out IReadOnlyList<DictionaryItemState>? items,
        out string? error)
    {
        try
        {
            items = _settingsService.LoadLegacyDictionaryItems();
            error = null;
            return true;
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            items = null;
            error = $"The legacy Dictionary settings copy could not be read: {ex.Message}";
            return false;
        }
    }

    private void TryClearLegacyItems(
        IReadOnlyList<DictionaryItemState>? legacyItems,
        ICollection<DictionaryContentIssue> issues)
    {
        if (legacyItems is null)
            return;

        try
        {
            _settingsService.ClearLegacyDictionaryItems();
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            issues.Add(new DictionaryContentIssue(
                _fileStore.PrimaryFilePath,
                $"Dictionary content is stored in its dedicated file, but the old settings copy could not be removed: {ex.Message}"));
        }
    }

    private static bool IsExpectedContentException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            SettingsPersistenceException or
            ArgumentException or
            NotSupportedException;
    }
}
