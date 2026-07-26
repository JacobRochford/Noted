using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Noted.Services;

namespace Noted;

public partial class NoteEditorWindow : Window
{
    private readonly INoteContentService _contentService;
    private string? _openFilePath;
    private string _savedContent = string.Empty;
    private bool _isDirty;
    private bool _isLoading;
    private bool _isDocumentReplacementPrepared;
    private bool _isPreparedForApplicationClose;

    public NoteEditorWindow(INoteContentService contentService)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        _contentService = contentService;

        InitializeComponent();
        UpdateEditorState();
    }

    public string? OpenFilePath => _openFilePath;

    public bool IsDirty => _isDirty;

    public bool IsWindowVisible => IsVisible;

    public bool OpenNote(string filePath)
    {
        _isDocumentReplacementPrepared = false;
        return OpenNoteCore(filePath, resolveCurrentChanges: true);
    }

    internal bool TryPrepareForDocumentReplacement()
    {
        if (_isDocumentReplacementPrepared)
            return true;

        if (!TryResolveDirtyChanges())
            return false;

        _isDocumentReplacementPrepared = true;
        return true;
    }

    internal bool OpenPreparedNote(string filePath)
    {
        if (!_isDocumentReplacementPrepared)
            throw new InvalidOperationException("A document replacement must be prepared before it is completed.");

        var opened = OpenNoteCore(filePath, resolveCurrentChanges: false);
        if (opened)
            _isDocumentReplacementPrepared = false;

        return opened;
    }

    internal void CancelPreparedDocumentReplacement()
    {
        _isDocumentReplacementPrepared = false;
    }

    internal bool TryPrepareForDocumentClear()
    {
        return TryResolveDirtyChanges();
    }

    private bool OpenNoteCore(string filePath, bool resolveCurrentChanges)
    {
        string normalizedPath;
        try
        {
            normalizedPath = NormalizePath(filePath);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ShowError("Unable to open note", ex.Message);
            return false;
        }

        if (PathsEqual(_openFilePath, normalizedPath))
        {
            ShowWindow();
            return true;
        }

        string content;
        try
        {
            content = _contentService.Load(normalizedPath);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ShowError("Unable to open note", ex.Message);
            return false;
        }

        if (resolveCurrentChanges && !TryResolveDirtyChanges())
            return false;

        SetDocument(normalizedPath, content);
        ShowWindow();
        return true;
    }

    public bool TryPrepareForClose()
    {
        if (!TryResolveDirtyChanges())
            return false;

        _isPreparedForApplicationClose = true;
        return true;
    }

    public void CancelPreparedClose()
    {
        _isPreparedForApplicationClose = false;
    }

    public bool IsEditingFile(string filePath)
    {
        try
        {
            return PathsEqual(_openFilePath, NormalizePath(filePath));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return false;
        }
    }

    public bool IsEditingWithinDirectory(string directoryPath)
    {
        if (_openFilePath is null)
            return false;

        try
        {
            return IsPathWithin(_openFilePath, NormalizePath(directoryPath));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return false;
        }
    }

    public bool TryPrepareForFileRemoval(string filePath)
    {
        return !IsEditingFile(filePath) || TryResolveDirtyChanges();
    }

    public bool TryPrepareForDirectoryRemoval(string directoryPath)
    {
        return !IsEditingWithinDirectory(directoryPath) || TryResolveDirtyChanges();
    }

    public void NotifyFileRemoved(string filePath)
    {
        if (!IsEditingFile(filePath))
            return;

        ClearDocument();
        Hide();
    }

    public void NotifyDirectoryRemoved(string directoryPath)
    {
        if (!IsEditingWithinDirectory(directoryPath))
            return;

        ClearDocument();
        Hide();
    }

    public void NotifyFileRenamed(string oldFilePath, string newFilePath)
    {
        if (!IsEditingFile(oldFilePath))
            return;

        _openFilePath = NormalizePath(newFilePath);
        UpdateEditorState();
    }

    public void NotifyDirectoryRenamed(string oldDirectoryPath, string newDirectoryPath)
    {
        if (_openFilePath is null)
            return;

        var normalizedOldDirectory = NormalizePath(oldDirectoryPath);
        if (!IsPathWithin(_openFilePath, normalizedOldDirectory))
            return;

        var relativePath = Path.GetRelativePath(normalizedOldDirectory, _openFilePath);
        _openFilePath = Path.GetFullPath(Path.Combine(NormalizePath(newDirectoryPath), relativePath));
        UpdateEditorState();
    }

    public void ClearDocument()
    {
        _isLoading = true;
        EditorTextBox.Clear();
        ResetUndoHistory();
        _isLoading = false;

        _openFilePath = null;
        _savedContent = string.Empty;
        _isDirty = false;
        _isDocumentReplacementPrepared = false;
        UpdateEditorState();
    }

    public void ShowWindow()
    {
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        EditorTextBox.Focus();
        Keyboard.Focus(EditorTextBox);
    }

    public void HideWindow()
    {
        Hide();
    }

    internal void MoveCaretToEnd()
    {
        EditorTextBox.CaretIndex = EditorTextBox.Text.Length;
        EditorTextBox.ScrollToEnd();
        EditorTextBox.Focus();
    }

    private void SetDocument(string filePath, string content)
    {
        _isLoading = true;
        EditorTextBox.Text = content;
        EditorTextBox.CaretIndex = 0;
        ResetUndoHistory();
        _isLoading = false;

        _openFilePath = filePath;
        _savedContent = content;
        _isDirty = false;
        UpdateEditorState();
    }

    private void ResetUndoHistory()
    {
        EditorTextBox.IsUndoEnabled = false;
        EditorTextBox.IsUndoEnabled = true;
    }

    private bool TryResolveDirtyChanges()
    {
        if (!_isDirty || _openFilePath is null)
            return true;

        var noteName = Path.GetFileNameWithoutExtension(_openFilePath);
        var result = ShowSavePrompt(noteName);

        return result switch
        {
            MessageBoxResult.Yes => TrySaveCurrentNote(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private bool TrySaveCurrentNote()
    {
        if (_openFilePath is null)
            return true;

        try
        {
            var content = EditorTextBox.Text;
            _contentService.Save(_openFilePath, content);
            _savedContent = content;
            _isDirty = false;
            UpdateEditorState();
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ShowError("Unable to save note", ex.Message);
            return false;
        }
    }

    private MessageBoxResult ShowSavePrompt(string noteName)
    {
        const string instructions =
            "Yes saves the note. No continues without saving and discards the changes only if the action completes. " +
            "Cancel keeps the note open.";
        var message = $"Save changes to '{noteName}'?\n\n{instructions}";

        return IsVisible
            ? MessageBox.Show(this, message, "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning)
            : MessageBox.Show(message, "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
    }

    private void ShowError(string title, string message)
    {
        if (IsVisible)
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void UpdateEditorState()
    {
        var hasDocument = _openFilePath is not null;
        var noteName = hasDocument ? Path.GetFileNameWithoutExtension(_openFilePath) : "No note open";

        DirtyStatusText.Visibility = _isDirty ? Visibility.Visible : Visibility.Collapsed;
        EditorTextBox.IsEnabled = hasDocument;
        SaveButton.IsEnabled = hasDocument && _isDirty;
        Title = hasDocument
            ? $"{(_isDirty ? "*" : string.Empty)}{noteName} - Noted"
            : "Note Editor - Noted";
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string? left, string right)
    {
        return left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithin(string filePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, filePath);
        return !Path.IsPathRooted(relativePath) &&
               !relativePath.Equals("..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;
    }

    private void EditorTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoading || _openFilePath is null)
            return;

        _isDirty = !string.Equals(EditorTextBox.Text, _savedContent, StringComparison.Ordinal);
        UpdateEditorState();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        TrySaveCurrentNote();
    }

    private void WordWrapMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = sender is MenuItem { IsChecked: true };
        EditorTextBox.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        EditorTextBox.HorizontalScrollBarVisibility = enabled
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        HeaderWordWrapMenuItem.IsChecked = enabled;
        ContextWordWrapMenuItem.IsChecked = enabled;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_openFilePath is not null)
            EditorTextBox.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        TrySaveCurrentNote();
        e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isPreparedForApplicationClose)
            return;

        e.Cancel = true;
        if (!TryResolveDirtyChanges())
            return;

        ClearDocument();
        Hide();
    }
}
