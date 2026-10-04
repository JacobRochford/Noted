using Noted.Models;

namespace Noted.Services;

public sealed record NoteEditorTabState
{
    public string FilePath { get; init; } = string.Empty;
    public IndentationSettings? Indentation { get; init; }
    public TextFileEncoding? PendingEncoding { get; init; }
    public int CaretIndex { get; init; }
    public double VerticalOffset { get; init; }
    public MarkdownPreviewMode MarkdownPreviewMode { get; init; }
    // Compatibility for callers and sessions using the former boolean mode.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MarkdownPreviewEnabled
    {
        get => MarkdownPreviewMode != MarkdownPreviewMode.Off;
        init
        {
            if (value && MarkdownPreviewMode == MarkdownPreviewMode.Off)
                MarkdownPreviewMode = MarkdownPreviewMode.Preview;
        }
    }
    [System.Text.Json.Serialization.JsonPropertyName("MarkdownPreviewEnabled")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyMarkdownPreviewEnabled
    {
        get => false;
        init => MarkdownPreviewEnabled = value;
    }
    public bool UsesGeneratedName { get; init; }
    public string? InitialFileContent { get; init; }
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
