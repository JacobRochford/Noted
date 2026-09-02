namespace Noted.Services;

public sealed record NoteEditorTabState
{
    public string FilePath { get; init; } = string.Empty;
    public int CaretIndex { get; init; }
    public double VerticalOffset { get; init; }
    public bool MarkdownPreviewEnabled { get; init; }
    public bool UsesGeneratedName { get; init; }
}

public sealed record NoteEditorSession
{
    public IReadOnlyList<NoteEditorTabState> Tabs { get; init; } = [];
    public string? ActiveFilePath { get; init; }
    public string? SecondaryFilePath { get; init; }
}

public sealed record NoteEditorSessionIssue(
    string FilePath,
    string Message);

public sealed record NoteEditorSessionLoadResult(
    NoteEditorSession Session,
    IReadOnlyList<NoteEditorSessionIssue> Issues);

public interface INoteEditorSessionService
{
    NoteEditorSessionLoadResult Load();
    string? Save(NoteEditorSession session);
}
