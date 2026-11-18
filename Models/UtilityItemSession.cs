namespace Noted.Models;

internal enum UtilityEditorField { None = 0, Primary = 1, Details = 2 }

internal sealed record UtilityEditorPosition(
    string TextHash, int CaretIndex, int SelectionStart, int SelectionLength,
    double HorizontalOffset, double VerticalOffset);

internal sealed record ChecklistEditSession(
    bool IsEditingTitle, string? OriginTabId, UtilityItemText Text, ChecklistCreationSession? Creation);

internal sealed record ChecklistCreationSession(ChecklistPriority Priority, string? TabId);

internal sealed record UtilityItemText(string PrimaryHash, string DetailsHash);

internal sealed record UtilityItemSession
{
    public Guid ItemId { get; init; }
    public bool IsExpanded { get; init; }
    public bool IsActive { get; init; }
    public UtilityEditorField ActiveEditor { get; init; }
    public UtilityEditorPosition? Primary { get; init; }
    public UtilityEditorPosition? Details { get; init; }
    public ChecklistEditSession? ChecklistEdit { get; init; }
    public UtilityItemText? DictionaryEdit { get; init; }
    public Guid? DictionaryDraftRevision { get; init; }
}
