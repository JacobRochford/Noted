using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Noted.Models;

public sealed class OpenNoteDocument : INotifyPropertyChanged
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
        bool isMissing = false)
    {
        _filePath = Path.GetFullPath(filePath);
        Content = content;
        SavedContent = savedContent;
        _isDirty = isDirty;
        _isMissing = isMissing;
        CaretIndex = caretIndex;
        VerticalOffset = verticalOffset;
        MarkdownPreviewEnabled = markdownPreviewEnabled;
    }

    public string FilePath => _filePath;
    public string DisplayName => Path.GetFileNameWithoutExtension(_filePath);
    public string TabLabel =>
        $"{DisplayName}{(_isMissing ? "  (missing)" : string.Empty)}{(_isDirty ? "  •" : string.Empty)}";
    public string Content { get; set; }
    public string SavedContent { get; set; }
    public int CaretIndex { get; set; }
    public double VerticalOffset { get; set; }
    public bool MarkdownPreviewEnabled { get; set; }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value)
                return;

            _isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabLabel));
        }
    }

    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing == value)
                return;

            _isMissing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabLabel));
        }
    }

    public void UpdateFilePath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (string.Equals(_filePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return;

        _filePath = normalizedPath;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
