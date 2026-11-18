namespace Noted.Models;

internal sealed record DictionaryDraftState
{
    public Guid DraftId { get; init; }
    public Guid RevisionId { get; init; }
    public Guid EntryId { get; init; }
    public DictionaryItemState? Baseline { get; init; }
    public string? Word { get; init; }
    public string? Definition { get; init; }
    public bool IsRetired { get; init; }

    internal static DictionaryDraftState Retire(Guid id) => new() { DraftId = id, IsRetired = true };
}
