using System.IO;
using Noted.Helpers;

namespace Noted.Models;

public sealed class OpenNoteDocument : ObservableObject
{
    private string _filePath;
    private bool _isDirty;
    private bool _isMissing;

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
        string? initialFileContent = null)
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
    }

    public Guid DocumentId { get; } = Guid.NewGuid();
    public string FilePath => _filePath;
    public string DisplayName => NoteNameFormatter.Format(
        Path.GetFileName(_filePath),
        keepExtensionForCustomName: false);
    public string TabLabel =>
        $"{DisplayName}{(_isMissing ? "  (missing)" : string.Empty)}{(_isDirty ? "  •" : string.Empty)}";
    public string Content { get; internal set; }
    public string SavedContent { get; internal set; }
    public int CaretIndex { get; internal set; }
    public double VerticalOffset { get; internal set; }
    public bool MarkdownPreviewEnabled { get; internal set; }
    public bool UsesGeneratedName { get; internal set; }
    // Non-null only for a file created by this app that has not been explicitly saved yet.
    public string? InitialFileContent { get; internal set; }

    public bool IsDirty
    {
        get => _isDirty;
        internal set
        {
            if (SetProperty(ref _isDirty, value))
                OnPropertyChanged(nameof(TabLabel));
        }
    }

    public bool IsMissing
    {
        get => _isMissing;
        internal set
        {
            if (SetProperty(ref _isMissing, value))
                OnPropertyChanged(nameof(TabLabel));
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
    }

}
