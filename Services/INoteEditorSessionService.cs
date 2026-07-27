namespace Noted.Services;

public sealed record NoteEditorTabState
{
    public string FilePath { get; init; } = string.Empty;
    public int CaretIndex { get; init; }
    public double VerticalOffset { get; init; }
    public bool MarkdownPreviewEnabled { get; init; }
}

public sealed record NoteEditorSession
{
    public IReadOnlyList<NoteEditorTabState> Tabs { get; init; } = [];
    public string? ActiveFilePath { get; init; }
}

public interface INoteEditorSessionService
{
    NoteEditorSession Load();
    void Save(NoteEditorSession session);
}
