namespace Noted.Models;

public sealed class NoteItem : ObservableObject
{
    private bool _isPinned;
    private string _displayName = "";
    private string _editableName = "";
    private string _subtitle = "";
    private bool _isEditing;
    private bool _isExpanded;
    private int _indentLevel;

    public string FileName { get; init; } = "";
    public string NoteKey { get; init; } = "";
    public DateTime LastModified { get; set; }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string EditableName
    {
        get => _editableName;
        set => SetProperty(ref _editableName, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public bool IsFolder { get; set; }

    // Only for folders, tracks expand/collapse in the tree.
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public int IndentLevel
    {
        get => _indentLevel;
        set => SetProperty(ref _indentLevel, value);
    }

    // Used for actions on child notes in expand mode.
    public string? FullPath { get; set; }

    public override string ToString() => DisplayName;
}
