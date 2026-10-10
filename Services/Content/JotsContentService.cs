using System.IO;
using Noted.Models;

namespace Noted.Services;

public sealed class JotsContentService : IJotsContentService
{
    private readonly JsonCollectionFileStore<JotItemState> _fileStore;

    public JotsContentService(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _fileStore = new JsonCollectionFileStore<JotItemState>(
            Path.Combine(Path.GetFullPath(storageDirectory), PersistencePaths.JotsFile),
            "Jots content");
    }

    public JotsContentLoadResult LoadItems()
    {
        var fileResult = _fileStore.Load();
        var issues = fileResult.Issues
            .Select(issue => new JotsContentIssue(issue.FilePath, issue.Message))
            .ToList();

        return new JotsContentLoadResult(fileResult.Items ?? [], issues);
    }

    public JotsContentSaveResult SaveItems(IReadOnlyList<JotItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new JotsContentSaveResult(_fileStore.Write(items));
    }
}
