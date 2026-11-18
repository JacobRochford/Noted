using System.IO;
using System.Text.Json;
using Noted.Models;

namespace Noted.Services;

internal sealed class DictionaryDraftStore(string directory)
{
    private readonly JsonCollectionFileStore<DictionaryDraftState> _store = new(
        Path.Combine(directory, PersistencePaths.DictionaryDraftsFile), "Dictionary drafts", Validate);

    internal JsonCollectionFileLoadResult<DictionaryDraftState> Load() => _store.Load();

    // a rolling copy of this file could bring back a retired draft.
    internal string? Save(IReadOnlyList<DictionaryDraftState> drafts) => _store.Write(drafts, updateBackup: false);

    internal static List<DictionaryDraftState> ReadBackup(byte[] bytes)
    {
        var drafts = JsonSerializer.Deserialize<List<DictionaryDraftState>>(bytes)
            ?? throw new InvalidDataException("The Dictionary draft collection is missing.");
        Validate(drafts);
        return drafts;
    }

    internal void MergeBackup(byte[]? bytes, IReadOnlySet<Guid> localCommitReceipts)
    {
        var incoming = bytes is null ? [] : ReadBackup(bytes);
        var local = Load();
        if (local.Issues.Count != 0)
            throw new IOException("The local Dictionary draft file could not be read safely.");
        var merged = (local.Items ?? []).ToDictionary(draft => draft.DraftId,
            draft => localCommitReceipts.Contains(draft.RevisionId) ? DictionaryDraftState.Retire(draft.DraftId) : draft);
        foreach (var draft in incoming)
        {
            if (!merged.TryGetValue(draft.DraftId, out var current))
            {
                merged.Add(draft.DraftId, draft);
                continue;
            }

            // the local record wins; keep a different incoming revision as a separate draft.
            if (!current.IsRetired && !draft.IsRetired && current.RevisionId != draft.RevisionId)
            {
                var conflict = draft with { DraftId = draft.RevisionId, EntryId = draft.RevisionId, Baseline = null };
                if (!merged.TryAdd(conflict.DraftId, conflict) &&
                    merged[conflict.DraftId] is { IsRetired: false } existing && existing != conflict)
                    throw new InvalidDataException("Conflicting Dictionary drafts have the same revision ID.");
            }
        }

        if (merged.Count == 0 && local.Items is null) return;
        var warning = Save(merged.Values.ToArray());
        if (warning is not null) throw new IOException(warning);
    }

    private static void Validate(IReadOnlyList<DictionaryDraftState> drafts)
    {
        var ids = new HashSet<Guid>();
        foreach (var draft in drafts)
        {
            if (draft is null || draft.DraftId == Guid.Empty || !ids.Add(draft.DraftId) ||
                (draft.IsRetired
                    ? draft.EntryId != Guid.Empty || draft.RevisionId != Guid.Empty ||
                      draft.Baseline is not null || draft.Word is not null || draft.Definition is not null
                    : draft.EntryId == Guid.Empty || draft.RevisionId == Guid.Empty ||
                      draft.Word is null || draft.Definition is null ||
                      draft.Baseline is not null && draft.Baseline.Id != draft.EntryId))
                throw new JsonException("The Dictionary draft collection contains an invalid record.");
        }
    }
}
