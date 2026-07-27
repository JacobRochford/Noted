using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class NoteEditorWindow : Window
{
    private const double DefaultTabsPanelWidth = 190;
    private const double MinimumTabsPanelWidth = 88;
    private const double MaximumTabsPanelWidth = 360;

    private readonly INoteContentService _contentService;
    private readonly IAppSettingsService _settingsService;
    private readonly INoteRecoveryService _recoveryService;
    private readonly INoteEditorSessionService _sessionService;
    private readonly DispatcherTimer _recoverySaveTimer;
    private readonly ObservableCollection<OpenNoteDocument> _documents = [];
    private readonly HashSet<OpenNoteDocument> _discardWhenPreparedActionCompletes = [];
    private OpenNoteDocument? _activeDocument;
    private OpenNoteDocument? _draggedDocument;
    private Point _tabDragStart;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isMarkdownPreviewEnabled;
    private bool _isPreparedForApplicationClose;
    private double _tabsPanelWidth = DefaultTabsPanelWidth;
    private bool _isTabsPanelCollapsed;
    private bool _suppressSessionSave;

    public NoteEditorWindow(
        INoteContentService contentService,
        IAppSettingsService settingsService,
        INoteRecoveryService recoveryService,
        INoteEditorSessionService sessionService)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(sessionService);

        _contentService = contentService;
        _settingsService = settingsService;
        _recoveryService = recoveryService;
        _sessionService = sessionService;
        _recoverySaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _recoverySaveTimer.Tick += RecoverySaveTimer_Tick;

        InitializeComponent();
        OpenTabsList.ItemsSource = _documents;

        var windowState = _settingsService.LoadNoteEditorWindowState();
        Width = NormalizeWindowDimension(windowState.Width, MinWidth, 900);
        Height = NormalizeWindowDimension(windowState.Height, MinHeight, 650);
        _tabsPanelWidth = NormalizeTabsPanelWidth(windowState.TabsPanelWidth);
        _isTabsPanelCollapsed = windowState.IsTabsPanelCollapsed;
        ApplyTabsPanelState();
        RestoreSessionMenuItem.IsChecked = _settingsService.LoadRestoreEditorSession();
        UpdateEditorState();
    }

    public string? OpenFilePath => _activeDocument?.FilePath;
    public bool IsDirty => _documents.Any(document => document.IsDirty);
    public bool IsWindowVisible => IsVisible;
    public event EventHandler? NewNoteRequested;

    public bool OpenNote(string filePath)
    {
        return OpenNoteCore(filePath);
    }

    internal bool TryPrepareForDocumentClear()
    {
        return TryResolveDirtyDocuments(_documents);
    }

    public bool TryPrepareForClose()
    {
        CaptureActiveDocument();
        FlushRecoveryDraft();
        if (!TryResolveDirtyDocuments(_documents))
            return false;

        SaveEditorSession();
        _isPreparedForApplicationClose = true;
        return true;
    }

    public void CancelPreparedClose()
    {
        _isPreparedForApplicationClose = false;
        _discardWhenPreparedActionCompletes.Clear();
    }

    internal void RestoreEditorSession()
    {
        var session = _settingsService.LoadRestoreEditorSession()
            ? _sessionService.Load()
            : new NoteEditorSession();
        var recoveredDrafts = _recoveryService.LoadDrafts()
            .ToDictionary(draft => draft.FilePath, StringComparer.OrdinalIgnoreCase);
        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _suppressSessionSave = true;
        try
        {
            foreach (var tabState in session.Tabs)
            {
                if (TryRestoreDocument(tabState, recoveredDrafts, out var document) &&
                    restoredPaths.Add(document.FilePath))
                {
                    _documents.Add(document);
                }
            }

            foreach (var draft in recoveredDrafts.Values)
            {
                if (restoredPaths.Contains(draft.FilePath) ||
                    !TryRestoreDraftOnlyDocument(draft, out var document))
                {
                    continue;
                }

                _documents.Add(document);
                restoredPaths.Add(document.FilePath);
            }

            if (_documents.Count == 0)
                return;

            var activeDocument = _documents.FirstOrDefault(document =>
                    PathsEqual(document.FilePath, session.ActiveFilePath))
                ?? _documents[0];
            ActivateDocument(activeDocument);
            ShowWindow();
        }
        finally
        {
            _suppressSessionSave = false;
        }

        SaveEditorSession();
    }

    public bool IsEditingFile(string filePath)
    {
        try
        {
            var normalizedPath = NormalizePath(filePath);
            return _documents.Any(document => PathsEqual(document.FilePath, normalizedPath));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return false;
        }
    }

    public bool IsEditingWithinDirectory(string directoryPath)
    {
        try
        {
            var normalizedDirectory = NormalizePath(directoryPath);
            return _documents.Any(document => IsPathWithin(document.FilePath, normalizedDirectory));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return false;
        }
    }

    public bool TryPrepareForFileRemoval(string filePath)
    {
        var document = FindDocument(filePath);
        return document is null || TryResolveDirtyDocuments([document]);
    }

    public bool TryPrepareForDirectoryRemoval(string directoryPath)
    {
        var normalizedDirectory = NormalizePath(directoryPath);
        var affectedDocuments = _documents
            .Where(document => IsPathWithin(document.FilePath, normalizedDirectory))
            .ToList();
        return TryResolveDirtyDocuments(affectedDocuments);
    }

    public void NotifyFileRemoved(string filePath)
    {
        var document = FindDocument(filePath);
        if (document is not null)
            RemoveDocument(document, deleteRecoveryDraft: true);
    }

    public void NotifyDirectoryRemoved(string directoryPath)
    {
        var normalizedDirectory = NormalizePath(directoryPath);
        var affectedDocuments = _documents
            .Where(document => IsPathWithin(document.FilePath, normalizedDirectory))
            .ToList();
        foreach (var document in affectedDocuments)
            RemoveDocument(document, deleteRecoveryDraft: true);
    }

    public void NotifyFileRenamed(string oldFilePath, string newFilePath)
    {
        var document = FindDocument(oldFilePath);
        if (document is null)
            return;

        CaptureActiveDocument();
        var oldPath = document.FilePath;
        document.UpdateFilePath(NormalizePath(newFilePath));
        document.IsMissing = false;
        if (document.IsDirty)
            MoveRecoveryDraft(oldPath, document);
        SaveEditorSession();
        UpdateEditorState();
    }

    public void NotifyDirectoryRenamed(string oldDirectoryPath, string newDirectoryPath)
    {
        var normalizedOldDirectory = NormalizePath(oldDirectoryPath);
        var normalizedNewDirectory = NormalizePath(newDirectoryPath);
        var affectedDocuments = _documents
            .Where(document => IsPathWithin(document.FilePath, normalizedOldDirectory))
            .ToList();

        CaptureActiveDocument();
        foreach (var document in affectedDocuments)
        {
            var oldPath = document.FilePath;
            var relativePath = Path.GetRelativePath(normalizedOldDirectory, oldPath);
            document.UpdateFilePath(Path.Combine(normalizedNewDirectory, relativePath));
            if (document.IsDirty)
                MoveRecoveryDraft(oldPath, document);
        }

        SaveEditorSession();
        UpdateEditorState();
    }

    public void ClearDocument()
    {
        _suppressSessionSave = true;
        try
        {
            foreach (var document in _documents.ToList())
                RemoveDocument(document, deleteRecoveryDraft: true, saveSession: false);
        }
        finally
        {
            _suppressSessionSave = false;
        }

        _discardWhenPreparedActionCompletes.Clear();
        ClearEditorSurface();
        SaveEditorSession();
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
        CaptureActiveDocument();
        FlushRecoveryDraft();
        SaveEditorSession();
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

        if (_activeDocument is not null)
            _activeDocument.CaretIndex = EditorTextBox.CaretIndex;
        FocusActiveSurface();
    }

    private bool OpenNoteCore(string filePath)
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

        var existingDocument = FindDocument(normalizedPath);
        if (existingDocument is not null)
        {
            ActivateDocument(existingDocument);
            ShowWindow();
            return true;
        }

        try
        {
            var persistedContent = _contentService.Load(normalizedPath);
            var draft = _recoveryService.LoadDraft(normalizedPath);
            var recoveredContent = draft is not null &&
                                   !string.Equals(draft.Content, persistedContent, StringComparison.Ordinal)
                ? draft.Content
                : persistedContent;
            if (draft is not null && string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
                _recoveryService.DeleteDraft(normalizedPath);

            var document = new OpenNoteDocument(
                normalizedPath,
                recoveredContent,
                persistedContent,
                isDirty: !string.Equals(recoveredContent, persistedContent, StringComparison.Ordinal));
            _documents.Add(document);
            ActivateDocument(document);
            SaveEditorSession();
            ShowWindow();
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ShowError("Unable to open note", ex.Message);
            return false;
        }
    }

    private bool TryRestoreDocument(
        NoteEditorTabState tabState,
        IReadOnlyDictionary<string, NoteRecoveryDraft> drafts,
        out OpenNoteDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(tabState.FilePath))
            return false;

        try
        {
            var normalizedPath = NormalizePath(tabState.FilePath);
            if (!File.Exists(normalizedPath))
            {
                if (!drafts.TryGetValue(normalizedPath, out var missingDraft))
                    return false;

                document = new OpenNoteDocument(
                    normalizedPath,
                    missingDraft.Content,
                    savedContent: string.Empty,
                    isDirty: true,
                    caretIndex: Math.Clamp(tabState.CaretIndex, 0, missingDraft.Content.Length),
                    verticalOffset: tabState.VerticalOffset,
                    markdownPreviewEnabled: tabState.MarkdownPreviewEnabled,
                    isMissing: true);
                return true;
            }

            var persistedContent = _contentService.Load(normalizedPath);
            drafts.TryGetValue(normalizedPath, out var draft);
            var content = draft is not null &&
                          !string.Equals(draft.Content, persistedContent, StringComparison.Ordinal)
                ? draft.Content
                : persistedContent;
            if (draft is not null && string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
                _recoveryService.DeleteDraft(normalizedPath);

            document = new OpenNoteDocument(
                normalizedPath,
                content,
                persistedContent,
                isDirty: !string.Equals(content, persistedContent, StringComparison.Ordinal),
                caretIndex: Math.Clamp(tabState.CaretIndex, 0, content.Length),
                verticalOffset: tabState.VerticalOffset,
                markdownPreviewEnabled: tabState.MarkdownPreviewEnabled);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private bool TryRestoreDraftOnlyDocument(
        NoteRecoveryDraft draft,
        out OpenNoteDocument document)
    {
        document = null!;
        try
        {
            if (!File.Exists(draft.FilePath))
            {
                document = new OpenNoteDocument(
                    draft.FilePath,
                    draft.Content,
                    savedContent: string.Empty,
                    isDirty: true,
                    caretIndex: draft.Content.Length,
                    isMissing: true);
                return true;
            }

            var persistedContent = _contentService.Load(draft.FilePath);
            if (string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
            {
                _recoveryService.DeleteDraft(draft.FilePath);
                return false;
            }

            document = new OpenNoteDocument(
                draft.FilePath,
                draft.Content,
                persistedContent,
                isDirty: true,
                caretIndex: draft.Content.Length);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private void ActivateDocument(OpenNoteDocument document)
    {
        if (ReferenceEquals(_activeDocument, document))
        {
            OpenTabsList.SelectedItem = document;
            FocusActiveSurface();
            return;
        }

        CaptureActiveDocument();
        FlushRecoveryDraft();
        _activeDocument = document;

        _isLoading = true;
        EditorTextBox.Text = document.Content;
        EditorTextBox.CaretIndex = Math.Clamp(document.CaretIndex, 0, document.Content.Length);
        EditorTextBox.ScrollToVerticalOffset(Math.Max(0, document.VerticalOffset));
        ResetUndoHistory();
        _isLoading = false;

        OpenTabsList.SelectedItem = document;
        SetMarkdownPreviewEnabled(document.MarkdownPreviewEnabled, saveSession: false);
        UpdateEditorState();
        SaveEditorSession();
        FocusActiveSurface();
    }

    private void CaptureActiveDocument()
    {
        if (_isLoading || _activeDocument is null)
            return;

        _activeDocument.Content = EditorTextBox.Text;
        _activeDocument.CaretIndex = EditorTextBox.CaretIndex;
        _activeDocument.VerticalOffset = EditorTextBox.VerticalOffset;
        _activeDocument.MarkdownPreviewEnabled = _isMarkdownPreviewEnabled;
    }

    private bool TryResolveDirtyDocuments(IEnumerable<OpenNoteDocument> documents)
    {
        CaptureActiveDocument();
        foreach (var document in documents.Where(document => document.IsDirty).ToList())
        {
            _discardWhenPreparedActionCompletes.Remove(document);
            var result = ShowSavePrompt(document.DisplayName);
            if (result == MessageBoxResult.Cancel)
                return false;
            if (result == MessageBoxResult.Yes && !TrySaveDocument(document))
                return false;
            if (result == MessageBoxResult.No)
                _discardWhenPreparedActionCompletes.Add(document);
        }

        return true;
    }

    private bool TrySaveCurrentNote()
    {
        return _activeDocument is null || TrySaveDocument(_activeDocument);
    }

    private bool TrySaveDocument(OpenNoteDocument document)
    {
        if (ReferenceEquals(document, _activeDocument))
            CaptureActiveDocument();

        if (document.IsMissing || !File.Exists(document.FilePath))
        {
            document.IsMissing = true;
            UpdateEditorState();
            ShowError(
                "Original note is missing",
                "The original file no longer exists. Its recovered text has not been discarded. " +
                "Copy the text into a new note before closing this tab.");
            return false;
        }

        try
        {
            _contentService.Save(document.FilePath, document.Content);
            document.SavedContent = document.Content;
            document.IsDirty = false;
            _discardWhenPreparedActionCompletes.Remove(document);
            _recoveryService.DeleteDraft(document.FilePath);
            if (ReferenceEquals(document, _activeDocument))
                UpdateEditorState();
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ShowError("Unable to save note", ex.Message);
            return false;
        }
    }

    private void RemoveDocument(
        OpenNoteDocument document,
        bool deleteRecoveryDraft,
        bool saveSession = true)
    {
        var index = _documents.IndexOf(document);
        if (deleteRecoveryDraft)
            _recoveryService.DeleteDraft(document.FilePath);

        _discardWhenPreparedActionCompletes.Remove(document);
        _documents.Remove(document);

        if (ReferenceEquals(_activeDocument, document))
        {
            _activeDocument = null;
            if (_documents.Count > 0)
                ActivateDocument(_documents[Math.Clamp(index, 0, _documents.Count - 1)]);
            else
            {
                ClearEditorSurface();
                Hide();
            }
        }

        if (saveSession)
            SaveEditorSession();
    }

    private void ClearEditorSurface()
    {
        _recoverySaveTimer.Stop();
        _activeDocument = null;
        _isLoading = true;
        EditorTextBox.Clear();
        ResetUndoHistory();
        _isLoading = false;
        OpenTabsList.SelectedItem = null;
        SetMarkdownPreviewEnabled(false, saveSession: false);
        UpdateEditorState();
    }

    private OpenNoteDocument? FindDocument(string filePath)
    {
        try
        {
            var normalizedPath = NormalizePath(filePath);
            return _documents.FirstOrDefault(document => PathsEqual(document.FilePath, normalizedPath));
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return null;
        }
    }

    private void ResetUndoHistory()
    {
        EditorTextBox.IsUndoEnabled = false;
        EditorTextBox.IsUndoEnabled = true;
    }

    private MessageBoxResult ShowSavePrompt(string noteName)
    {
        var message =
            $"Save changes to '{noteName}'?\n\n" +
            "Yes saves the note. No discards its unsaved changes when the action completes. " +
            "Cancel keeps the note open.";
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
        var hasDocument = _activeDocument is not null;
        DirtyStatusText.Visibility = _activeDocument?.IsDirty == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        DirtyStatusText.Text = _activeDocument?.IsMissing == true
            ? "Recovered changes - original file missing"
            : "Unsaved changes";
        EditorTextBox.IsEnabled = hasDocument;
        EditorTextBox.IsReadOnly = _activeDocument?.IsMissing == true;
        SaveButton.IsEnabled = _activeDocument is { IsDirty: true, IsMissing: false };
        Title = hasDocument
            ? $"{(_activeDocument!.IsDirty ? "*" : string.Empty)}{_activeDocument.DisplayName} - Noted"
            : "Noted";
    }

    private void ScheduleRecoveryDraftSave()
    {
        _recoverySaveTimer.Stop();
        if (_activeDocument?.IsDirty == true)
        {
            _recoverySaveTimer.Start();
            return;
        }

        if (_activeDocument is not null)
            _recoveryService.DeleteDraft(_activeDocument.FilePath);
    }

    private void RecoverySaveTimer_Tick(object? sender, EventArgs e)
    {
        _recoverySaveTimer.Stop();
        FlushRecoveryDraft();
    }

    private void FlushRecoveryDraft()
    {
        _recoverySaveTimer.Stop();
        CaptureActiveDocument();
        if (_activeDocument?.IsDirty != true)
            return;

        try
        {
            _recoveryService.SaveDraft(_activeDocument.FilePath, _activeDocument.Content);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void MoveRecoveryDraft(string oldFilePath, OpenNoteDocument document)
    {
        try
        {
            _recoveryService.MoveDraft(oldFilePath, document.FilePath);
            _recoveryService.SaveDraft(document.FilePath, document.Content);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void SaveEditorSession()
    {
        if (_suppressSessionSave)
            return;

        CaptureActiveDocument();
        try
        {
            _sessionService.Save(new NoteEditorSession
            {
                Tabs = _documents.Select(document => new NoteEditorTabState
                {
                    FilePath = document.FilePath,
                    CaretIndex = document.CaretIndex,
                    VerticalOffset = document.VerticalOffset,
                    MarkdownPreviewEnabled = document.MarkdownPreviewEnabled
                }).ToList(),
                ActiveFilePath = _activeDocument?.FilePath
            });
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void SaveWindowSize()
    {
        if (!_hasLoaded)
            return;

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        CaptureTabsPanelWidth();
        try
        {
            _settingsService.SaveNoteEditorWindowState(new NoteEditorWindowState
            {
                Width = NormalizeWindowDimension(bounds.Width, MinWidth, 900),
                Height = NormalizeWindowDimension(bounds.Height, MinHeight, 650),
                TabsPanelWidth = _tabsPanelWidth,
                IsTabsPanelCollapsed = _isTabsPanelCollapsed
            });
        }
        catch (SettingsPersistenceException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _activeDocument is null)
            return;

        _activeDocument.Content = EditorTextBox.Text;
        _activeDocument.CaretIndex = EditorTextBox.CaretIndex;
        _activeDocument.IsDirty = !string.Equals(
            _activeDocument.Content,
            _activeDocument.SavedContent,
            StringComparison.Ordinal);
        ScheduleRecoveryDraftSave();
        UpdateMarkdownPreview();
        UpdateEditorState();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        TrySaveCurrentNote();
    }

    private void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        RequestNewNote();
    }

    private void RequestNewNote()
    {
        NewNoteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && OpenTabsList.SelectedItem is OpenNoteDocument document)
            ActivateDocument(document);
    }

    private void OpenTabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(OpenTabsList);
        _draggedDocument = FindDocumentFromElement(e.OriginalSource as DependencyObject);
    }

    private void OpenTabsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedDocument is null)
            return;

        var position = e.GetPosition(OpenTabsList);
        if (Math.Abs(position.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(OpenTabsList, _draggedDocument, DragDropEffects.Move);
    }

    private void OpenTabsList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(OpenNoteDocument)) ||
            e.Data.GetData(typeof(OpenNoteDocument)) is not OpenNoteDocument source)
        {
            return;
        }

        var target = FindDocumentFromElement(e.OriginalSource as DependencyObject);
        if (target is null || ReferenceEquals(source, target))
            return;

        var targetIndex = _documents.IndexOf(target);
        _documents.Move(_documents.IndexOf(source), targetIndex);
        OpenTabsList.SelectedItem = _activeDocument;
        SaveEditorSession();
    }

    private OpenNoteDocument? FindDocumentFromElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: OpenNoteDocument document })
                return document;
            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OpenNoteDocument document })
            return;
        if (document.IsDirty && !TryResolveDirtyDocuments([document]))
            return;

        RemoveDocument(document, deleteRecoveryDraft: true);
        e.Handled = true;
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

    private void RestoreSessionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = sender is MenuItem { IsChecked: true };
        try
        {
            _settingsService.SaveRestoreEditorSession(enabled);
            if (enabled)
                SaveEditorSession();
        }
        catch (SettingsPersistenceException ex)
        {
            RestoreSessionMenuItem.IsChecked = !enabled;
            ShowError("Unable to save setting", ex.Message);
        }
    }

    private void TabsPanelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetTabsPanelCollapsed(sender is not MenuItem { IsChecked: true });
    }

    private void ToggleTabsButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTabsPanel();
    }

    private void TabsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        CaptureTabsPanelWidth();
        SaveWindowSize();
    }

    private void ToggleTabsPanel()
    {
        SetTabsPanelCollapsed(!_isTabsPanelCollapsed);
    }

    private void SetTabsPanelCollapsed(bool collapsed)
    {
        if (collapsed)
            CaptureTabsPanelWidth();

        _isTabsPanelCollapsed = collapsed;
        ApplyTabsPanelState();
        SaveWindowSize();
        FocusActiveSurface();
    }

    private void ApplyTabsPanelState()
    {
        TabsPanel.Visibility = _isTabsPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        TabsSplitter.Visibility = _isTabsPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_isTabsPanelCollapsed)
        {
            TabsColumn.MinWidth = 0;
            TabsColumn.MaxWidth = 0;
            TabsColumn.Width = new GridLength(0);
            TabsSplitterColumn.Width = new GridLength(0);
        }
        else
        {
            TabsColumn.MaxWidth = MaximumTabsPanelWidth;
            TabsColumn.MinWidth = MinimumTabsPanelWidth;
            TabsColumn.Width = new GridLength(_tabsPanelWidth);
            TabsSplitterColumn.Width = new GridLength(5);
        }
        TabsPanelMenuItem.IsChecked = !_isTabsPanelCollapsed;
        TabsPanelIconSidebar.Opacity = _isTabsPanelCollapsed ? 0.25 : 1;
        ToggleTabsButton.ToolTip = _isTabsPanelCollapsed
            ? "Show tabs (Ctrl+B)"
            : "Hide tabs (Ctrl+B)";
    }

    private void CaptureTabsPanelWidth()
    {
        if (_isTabsPanelCollapsed || TabsColumn.ActualWidth <= 0)
            return;

        _tabsPanelWidth = NormalizeTabsPanelWidth(TabsColumn.ActualWidth);
    }

    private void ToggleMarkdownPreview()
    {
        SetMarkdownPreviewEnabled(!_isMarkdownPreviewEnabled);
    }

    private void SetMarkdownPreviewEnabled(bool enabled, bool saveSession = true)
    {
        _isMarkdownPreviewEnabled = enabled;
        if (_activeDocument is not null)
            _activeDocument.MarkdownPreviewEnabled = enabled;
        HeaderMarkdownMenuItem.IsChecked = enabled;
        ContextMarkdownMenuItem.IsChecked = enabled;
        PreviewMarkdownMenuItem.IsChecked = enabled;
        EditorTextBox.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        MarkdownPreview.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (enabled)
            UpdateMarkdownPreview();
        if (saveSession)
            SaveEditorSession();
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
        PositionOnPreferredDisplay();
        FocusActiveSurface();
    }

    private void PositionOnPreferredDisplay()
    {
        var screens = DisplayMonitorService.GetDisplays();
        var preferredDeviceName = _settingsService.LoadPreferredDisplayDeviceName();
        var screen = !string.IsNullOrWhiteSpace(preferredDeviceName)
            ? screens.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.DeviceName,
                    preferredDeviceName,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        screen ??= screens.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? screens.FirstOrDefault();
        if (screen is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var workLeft = screen.WorkLeft / dpi.DpiScaleX;
        var workTop = screen.WorkTop / dpi.DpiScaleY;
        var workWidth = screen.WorkWidth / dpi.DpiScaleX;
        var workHeight = screen.WorkHeight / dpi.DpiScaleY;
        Left = workLeft + Math.Max(0, (workWidth - ActualWidth) / 2);
        Top = workTop + Math.Max(0, (workHeight - ActualHeight) / 2);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.V && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleMarkdownPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && modifiers == ModifierKeys.Control)
        {
            TrySaveCurrentNote();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N && modifiers == ModifierKeys.Control)
        {
            RequestNewNote();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.B && modifiers == ModifierKeys.Control)
        {
            ToggleTabsPanel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.W && modifiers == ModifierKeys.Control && _activeDocument is not null)
        {
            if (!_activeDocument.IsDirty || TryResolveDirtyDocuments([_activeDocument]))
                RemoveDocument(_activeDocument, deleteRecoveryDraft: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab &&
            (modifiers == ModifierKeys.Control ||
             modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            CycleActiveDocument(backward: modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    private void CycleActiveDocument(bool backward)
    {
        if (_activeDocument is null || _documents.Count < 2)
            return;

        var currentIndex = _documents.IndexOf(_activeDocument);
        var nextIndex = backward
            ? (currentIndex - 1 + _documents.Count) % _documents.Count
            : (currentIndex + 1) % _documents.Count;
        ActivateDocument(_documents[nextIndex]);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowSize();
        CaptureActiveDocument();
        FlushRecoveryDraft();
        SaveEditorSession();

        if (_isPreparedForApplicationClose)
        {
            foreach (var document in _discardWhenPreparedActionCompletes)
                _recoveryService.DeleteDraft(document.FilePath);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return left is not null &&
               right is not null &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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

    private static double NormalizeTabsPanelWidth(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumTabsPanelWidth, MaximumTabsPanelWidth)
            : DefaultTabsPanelWidth;
    }
}
