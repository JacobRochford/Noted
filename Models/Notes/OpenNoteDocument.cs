using System.IO;
using Noted.Helpers;

namespace Noted.Models;

public sealed class OpenNoteDocument : ObservableObject
{
    private string _filePath;
    private bool _isDirty;
    private bool _isMissing;
    private string _content = "";
    private bool _usesGeneratedName;
    private string? _initialFileContent;

    public OpenNoteDocument(
        string filePath,
        string content,
        string savedContent,
        bool isDirty,
        int caretIndex = 0,
        double verticalOffset = 0,
        bool markdownPreviewEnabled = false,
        bool isMissing = false,
        bool usesGeneratedName = false,
        string? initialFileContent = null,
        IndentationSettings? indentation = null,
        TextFileEncoding encoding = TextFileEncoding.Utf8)
    {
        _filePath = Path.GetFullPath(filePath);
        Content = content;
        SavedContent = savedContent;
        _isDirty = isDirty;
        _isMissing = isMissing;
        CaretIndex = caretIndex;
        VerticalOffset = verticalOffset;
        MarkdownPreviewEnabled = markdownPreviewEnabled;
        UsesGeneratedName = usesGeneratedName;
        InitialFileContent = initialFileContent;
        Indentation = indentation ?? new();
        Encoding = encoding;
        SavedEncoding = encoding;
    }

    public IndentationSettings Indentation { get; internal set; }

    public Guid DocumentId { get; } = Guid.NewGuid();
    public string FilePath => _filePath;
    public string DisplayName => NoteNameFormatter.Format(
        Path.GetFileName(_filePath),
        keepExtensionForCustomName: false);
    public string TabLabel =>
        $"{(_isDirty ? "•  " : string.Empty)}{DisplayName}{(_isMissing ? "  (missing)" : string.Empty)}";
    public string SidebarLabel
    {
        get
        {
            var label = UsesGeneratedName && InitialFileContent is not null
                ? NoteSidebarLabel.GetContentLabel(Content, InitialFileContent) : string.Empty;
            if (label.Length == 0) label = DisplayName;
            return $"{(_isDirty ? "•  " : string.Empty)}{label}{(_isMissing ? "  (missing)" : string.Empty)}";
        }
    }
    public string Content
    {
        get => _content;
        internal set
        {
            if (SetProperty(ref _content, value)) OnPropertyChanged(nameof(SidebarLabel));
        }
    }
    public string SavedContent { get; internal set; }
    public TextFileEncoding Encoding { get; internal set; }
    public TextFileEncoding SavedEncoding { get; internal set; }
    public int CaretIndex { get; internal set; }
    public double VerticalOffset { get; internal set; }
    public MarkdownPreviewMode MarkdownPreviewMode { get; internal set; }
    public bool MarkdownPreviewEnabled
    {
        get => MarkdownPreviewMode != MarkdownPreviewMode.Off;
        internal set => MarkdownPreviewMode = value ? MarkdownPreviewMode.Preview : MarkdownPreviewMode.Off;
    }
    public bool UsesGeneratedName
    {
        get => _usesGeneratedName;
        internal set
        {
            if (SetProperty(ref _usesGeneratedName, value)) OnPropertyChanged(nameof(SidebarLabel));
        }
    }
    // Non-null only for a file created by this app that has not been explicitly saved yet.
    public string? InitialFileContent
    {
        get => _initialFileContent;
        internal set
        {
            if (SetProperty(ref _initialFileContent, value)) OnPropertyChanged(nameof(SidebarLabel));
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        internal set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(TabLabel));
                OnPropertyChanged(nameof(SidebarLabel));
            }
        }
    }

    public bool IsMissing
    {
        get => _isMissing;
        internal set
        {
            if (SetProperty(ref _isMissing, value))
            {
                OnPropertyChanged(nameof(TabLabel));
                OnPropertyChanged(nameof(SidebarLabel));
            }
        }
    }

    internal void UpdateFilePath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (string.Equals(_filePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return;

        _filePath = normalizedPath;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabLabel));
        OnPropertyChanged(nameof(SidebarLabel));
    }

}
