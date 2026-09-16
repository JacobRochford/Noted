using System.IO;
using Noted.Models;

namespace Noted.Services;

public sealed class DictionaryContentService : IDictionaryContentService
{
    private readonly JsonCollectionFileStore<DictionaryItemState> _fileStore;

    public DictionaryContentService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
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

        return new DictionaryContentLoadResult(fileResult.Items ?? [], issues);
    }

    public DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new DictionaryContentSaveResult(_fileStore.Write(items));
    }
}
