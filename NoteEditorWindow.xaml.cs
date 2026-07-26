using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Services;

namespace Noted;

public partial class NoteEditorWindow : Window
{
    private readonly INoteContentService _contentService;
    private readonly IAppSettingsService _settingsService;
    private readonly INoteRecoveryService _recoveryService;
    private readonly DispatcherTimer _recoverySaveTimer;
    private string? _openFilePath;
    private string _savedContent = string.Empty;
    private bool _isDirty;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isMarkdownPreviewEnabled;
    private bool _isDocumentReplacementPrepared;
    private bool _isPreparedForApplicationClose;
    private bool _discardRecoveryWhenActionCompletes;

    public NoteEditorWindow(
        INoteContentService contentService,
        IAppSettingsService settingsService,
        INoteRecoveryService recoveryService)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(recoveryService);
        _contentService = contentService;
        _settingsService = settingsService;
        _recoveryService = recoveryService;
        _recoverySaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _recoverySaveTimer.Tick += RecoverySaveTimer_Tick;

        InitializeComponent();
        var windowState = _settingsService.LoadNoteEditorWindowState();
        Width = NormalizeWindowDimension(windowState.Width, MinWidth, 900);
        Height = NormalizeWindowDimension(windowState.Height, MinHeight, 650);
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
        _discardRecoveryWhenActionCompletes = false;
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
        _discardRecoveryWhenActionCompletes = false;
    }

    internal bool TryRestoreRecoveryDraft()
    {
        var draft = _recoveryService.LoadDraft();
        if (draft is null || !File.Exists(draft.FilePath))
            return false;

        try
        {
            var persistedContent = _contentService.Load(draft.FilePath);
            if (string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
            {
                _recoveryService.DeleteDraft(draft.FilePath);
                return false;
            }

            SetRecoveredDocument(draft.FilePath, persistedContent, draft.Content);
            ShowWindow();
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
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

        var normalizedOldPath = _openFilePath!;
        _openFilePath = NormalizePath(newFilePath);
        if (_isDirty)
            MoveRecoveryDraft(normalizedOldPath, _openFilePath);
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
        var previousPath = _openFilePath;
        _openFilePath = Path.GetFullPath(Path.Combine(NormalizePath(newDirectoryPath), relativePath));
        if (_isDirty)
            MoveRecoveryDraft(previousPath, _openFilePath);
        UpdateEditorState();
    }

    public void ClearDocument()
    {
        DeleteRecoveryDraft();
        _isLoading = true;
        EditorTextBox.Clear();
        ResetUndoHistory();
        _isLoading = false;

        _openFilePath = null;
        _savedContent = string.Empty;
        _isDirty = false;
        _isDocumentReplacementPrepared = false;
        _discardRecoveryWhenActionCompletes = false;
        UpdateMarkdownPreview();
        UpdateEditorState();
    }

    public void ShowWindow()
    {
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        FocusActiveSurface();
    }

    public void HideWindow()
    {
        Hide();
    }

    internal void BeginEditingCreatedNote(bool moveCaretToEnd)
    {
        SetMarkdownPreviewEnabled(false);
        if (moveCaretToEnd)
        {
            EditorTextBox.CaretIndex = EditorTextBox.Text.Length;
            EditorTextBox.ScrollToEnd();
        }
        FocusActiveSurface();
    }

    private void SetDocument(string filePath, string content)
    {
        DeleteRecoveryDraft();
        _isLoading = true;
        EditorTextBox.Text = content;
        EditorTextBox.CaretIndex = 0;
        ResetUndoHistory();
        _isLoading = false;

        _openFilePath = filePath;
        _savedContent = content;
        _isDirty = false;
        _discardRecoveryWhenActionCompletes = false;
        UpdateMarkdownPreview();
        UpdateEditorState();
    }

    private void SetRecoveredDocument(string filePath, string persistedContent, string recoveredContent)
    {
        _isLoading = true;
        EditorTextBox.Text = recoveredContent;
        EditorTextBox.CaretIndex = recoveredContent.Length;
        ResetUndoHistory();
        _isLoading = false;

        _openFilePath = NormalizePath(filePath);
        _savedContent = persistedContent;
        _isDirty = true;
        _discardRecoveryWhenActionCompletes = false;
        UpdateMarkdownPreview();
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

        if (result == MessageBoxResult.Yes)
        {
            _discardRecoveryWhenActionCompletes = false;
            return TrySaveCurrentNote();
        }

        _discardRecoveryWhenActionCompletes = result == MessageBoxResult.No;
        return _discardRecoveryWhenActionCompletes;
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
            _discardRecoveryWhenActionCompletes = false;
            DeleteRecoveryDraft();
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

    private static double NormalizeWindowDimension(double value, double minimum, double fallback)
    {
        return double.IsFinite(value) && value >= minimum ? value : fallback;
    }

    private void SaveWindowSize()
    {
        if (!_hasLoaded)
            return;

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        try
        {
            _settingsService.SaveNoteEditorWindowState(new NoteEditorWindowState
            {
                Width = NormalizeWindowDimension(bounds.Width, MinWidth, 900),
                Height = NormalizeWindowDimension(bounds.Height, MinHeight, 650)
            });
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void EditorTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoading || _openFilePath is null)
            return;

        _isDirty = !string.Equals(EditorTextBox.Text, _savedContent, StringComparison.Ordinal);
        ScheduleRecoveryDraftSave();
        UpdateMarkdownPreview();
        UpdateEditorState();
    }

    private void ScheduleRecoveryDraftSave()
    {
        _recoverySaveTimer.Stop();
        if (_isDirty && _openFilePath is not null)
        {
            _recoverySaveTimer.Start();
            return;
        }

        DeleteRecoveryDraft();
    }

    private void RecoverySaveTimer_Tick(object? sender, EventArgs e)
    {
        _recoverySaveTimer.Stop();
        SaveRecoveryDraft();
    }

    private void SaveRecoveryDraft()
    {
        if (!_isDirty || _openFilePath is null)
            return;

        try
        {
            _recoveryService.SaveDraft(_openFilePath, EditorTextBox.Text);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void DeleteRecoveryDraft()
    {
        _recoverySaveTimer.Stop();
        if (_openFilePath is not null)
            _recoveryService.DeleteDraft(_openFilePath);
    }

    private void MoveRecoveryDraft(string oldFilePath, string newFilePath)
    {
        _recoverySaveTimer.Stop();
        try
        {
            _recoveryService.MoveDraft(oldFilePath, newFilePath);
            _recoveryService.SaveDraft(newFilePath, EditorTextBox.Text);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
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

    private void MarkdownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetMarkdownPreviewEnabled(sender is MenuItem { IsChecked: true });
    }

    private void ToggleMarkdownPreview()
    {
        SetMarkdownPreviewEnabled(!_isMarkdownPreviewEnabled);
    }

    private void SetMarkdownPreviewEnabled(bool enabled)
    {
        _isMarkdownPreviewEnabled = enabled;
        HeaderMarkdownMenuItem.IsChecked = enabled;
        ContextMarkdownMenuItem.IsChecked = enabled;
        PreviewMarkdownMenuItem.IsChecked = enabled;
        EditorTextBox.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        MarkdownPreview.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (enabled)
            UpdateMarkdownPreview();

        FocusActiveSurface();
    }

    private void UpdateMarkdownPreview()
    {
        if (_isMarkdownPreviewEnabled)
            MarkdownPreview.Document = MarkdownRenderer.Render(EditorTextBox.Text);
    }

    private void FocusActiveSurface()
    {
        if (_isMarkdownPreviewEnabled)
        {
            MarkdownPreview.Focus();
            Keyboard.Focus(MarkdownPreview);
            return;
        }

        EditorTextBox.Focus();
        Keyboard.Focus(EditorTextBox);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _hasLoaded = true;
        if (_openFilePath is not null)
            FocusActiveSurface();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V &&
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleMarkdownPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TrySaveCurrentNote();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowSize();

        if (_isPreparedForApplicationClose)
        {
            if (_discardRecoveryWhenActionCompletes)
                DeleteRecoveryDraft();
            return;
        }

        e.Cancel = true;
        if (!TryResolveDirtyChanges())
            return;

        ClearDocument();
        Hide();
    }
}
