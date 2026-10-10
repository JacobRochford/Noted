using System.IO;
using Noted.Models;

namespace Noted.Services;

internal sealed class JotCaptureDraftService
{
    private readonly JsonCollectionFileStore<JotCaptureDraft> _fileStore;

    internal JotCaptureDraftService(string directory)
    {
        _fileStore = new JsonCollectionFileStore<JotCaptureDraft>(
            Path.Combine(directory, PersistencePaths.JotCaptureDraftFile), "Jot capture draft");
    }

    internal JotCaptureDraft Load(out string? warning)
    {
        var result = _fileStore.Load();
        warning = result.Issues.Count == 0 ? null : string.Join(" ", result.Issues.Select(issue => issue.Message));
        var draft = result.Items?.FirstOrDefault() ?? new JotCaptureDraft();
        return draft with { Id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id, Text = draft.Text ?? "" };
    }

    internal string? Save(JotCaptureDraft draft) => _fileStore.Write([draft]);
}
