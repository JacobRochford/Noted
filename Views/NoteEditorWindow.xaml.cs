using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly SaveScheduler<IReadOnlyList<RecoveryDraftSnapshot>> _recoverySaveScheduler;
    private readonly ObservableCollection<OpenNoteDocument> _documents = [];
    private readonly HashSet<OpenNoteDocument> _discardedDocumentsPendingDraftDeletion = [];
    private readonly HashSet<string> _unsavedRecoveredPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private OpenNoteDocument? _activeDocument;
    private OpenNoteDocument? _draggedDocument;
    private Point _tabDragStart;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isMarkdownPreviewEnabled;
    private bool _isPreparedForApplicationClose;
    private double _tabsPanelWidth = DefaultTabsPanelWidth;
    private bool _isTabsPanelCollapsed;
    private bool _reopenOnStartup;
    private bool _isHiddenTogether;
    private bool _suppressSessionSave;
    private bool _recoveryIssuesFoundThisRun;
    private string? _recoveryOperationError;
    private string? _sessionPersistenceError;
    private string? _noteSaveWarning;

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

        InitializeComponent();
        _recoverySaveScheduler = new SaveScheduler<IReadOnlyList<RecoveryDraftSnapshot>>(
            Dispatcher,
            quietPeriod: TimeSpan.FromMilliseconds(750),
            maximumDelay: TimeSpan.FromSeconds(2),
            SaveRecoveryDraftSnapshots);
        _recoverySaveScheduler.StateChanged += RecoverySaveScheduler_StateChanged;
        Closed += Window_Closed;
        OpenTabsList.ItemsSource = _documents;

        var windowState = _settingsService.LoadNoteEditorWindowState();
        Width = NormalizeWindowDimension(windowState.Width, MinWidth, 900);
        Height = NormalizeWindowDimension(windowState.Height, MinHeight, 650);
        _tabsPanelWidth = NormalizeTabsPanelWidth(windowState.TabsPanelWidth);
        _isTabsPanelCollapsed = windowState.IsTabsPanelCollapsed;
        _reopenOnStartup = windowState.ReopenOnStartup;
        ApplyTabsPanelState();
        SetWordWrapEnabled(windowState.WordWrapEnabled);
        ReopenTabsOnStartupMenuItem.IsChecked = _settingsService.LoadReopenEditorTabsOnStartup();
        UpdateEditorState();
    }

    public string? OpenFilePath => _activeDocument?.FilePath;
    public bool IsDirty => _documents.Any(document => document.IsDirty);
    public bool IsWindowVisible => IsVisible;
    internal bool RecoveryBlocksBackup =>
        _unsavedRecoveredPaths.Count > 0 || _recoveryIssuesFoundThisRun;

    internal string? BackupBlockingIssue => GetPersistenceIssues().FirstOrDefault();
    public event EventHandler? NewNoteRequested;
    public event EventHandler<NoteDeleteRequestedEventArgs>? DeleteNoteRequested;

    public bool OpenNote(string filePath)
    {
        return OpenNoteCore(filePath);
    }

    internal bool TryPrepareToCloseAllDocuments()
    {
        return TryResolveDirtyDocuments(_documents);
    }

    public bool TryPrepareForClose()
    {
        var reopenOnStartup = IsVisible || _isHiddenTogether;
        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        if (!TryResolveDirtyDocuments(_documents))
            return false;
        if (!SaveEditorSession())
        {
            ShowError(
                "Unable to save editor session",
                $"Noted could not close because the editor session was not saved.\n\n{_sessionPersistenceError}");
            return false;
        }
        if (!TryDeleteDiscardedRecoveryDrafts())
            return false;

        _isPreparedForApplicationClose = true;
        _reopenOnStartup = reopenOnStartup;
        SaveWindowSize();
        return true;
    }

    internal bool TryFlushForBackup(out string? error)
    {
        if (!TryFlushRecoveryDraft(out error))
            return false;

        if (!SaveEditorSession())
        {
            error = _sessionPersistenceError ?? "The note editor session could not be saved.";
            return false;
        }

        error = BackupBlockingIssue;
        return error is null;
    }

    public void CancelPreparedClose()
    {
        _isPreparedForApplicationClose = false;
        _discardedDocumentsPendingDraftDeletion.Clear();
        ScheduleRecoveryDraftSave();
    }

    internal void ReopenSavedTabs()
    {
        var sessionLoadResult = _settingsService.LoadReopenEditorTabsOnStartup()
            ? _sessionService.Load()
            : new NoteEditorSessionLoadResult(new NoteEditorSession(), []);
        var recoveryLoadResult = _recoveryService.LoadDrafts();
        var recoveryIssues = recoveryLoadResult.Issues.ToList();
        var session = sessionLoadResult.Session;
        var recoveredDrafts = recoveryLoadResult.Drafts
            .ToDictionary(draft => draft.FilePath, StringComparer.OrdinalIgnoreCase);
        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _suppressSessionSave = true;
        try
        {
            foreach (var tabState in session.Tabs)
            {
                if (TryRestoreDocument(tabState, recoveredDrafts, recoveryIssues, out var document) &&
                    restoredPaths.Add(document.FilePath))
                {
                    _documents.Add(document);
                }
            }

            foreach (var draft in recoveredDrafts.Values)
            {
                if (restoredPaths.Contains(draft.FilePath) ||
                    !TryRestoreDraftOnlyDocument(draft, recoveryIssues, out var document))
                {
                    continue;
                }

                _documents.Add(document);
                restoredPaths.Add(document.FilePath);
            }

            if (_documents.Count > 0)
            {
                var activeDocument = _documents.FirstOrDefault(document =>
                        PathsEqual(document.FilePath, session.ActiveFilePath))
                    ?? _documents[0];
                ActivateDocument(activeDocument);
            }
        }
        finally
        {
            _suppressSessionSave = false;
        }

        if (_documents.Count > 0)
            SaveEditorSession();

        foreach (var document in _documents.Where(document => document.IsDirty))
            _unsavedRecoveredPaths.Add(document.FilePath);

        ShowRecoveryIssues(recoveryIssues, sessionLoadResult.Issues);
        if (_documents.Count > 0 &&
            (_reopenOnStartup ||
             _unsavedRecoveredPaths.Count > 0 ||
             recoveryIssues.Count > 0 ||
             sessionLoadResult.Issues.Count > 0))
        {
            ShowWindow();
        }
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
        {
            if (!RemoveDocument(document, deleteRecoveryDraft: true))
            {
                document.IsMissing = true;
                UpdateEditorState();
            }
            return;
        }

        if (!TryDeleteRecoveryDraft(filePath, out var error))
            ShowRecoveryCleanupWarning(error!);
    }

    public void NotifyDirectoryRemoved(string directoryPath)
    {
        var normalizedDirectory = NormalizePath(directoryPath);
        var affectedDocuments = _documents
            .Where(document => IsPathWithin(document.FilePath, normalizedDirectory))
            .ToList();
        foreach (var document in affectedDocuments)
        {
            if (!RemoveDocument(document, deleteRecoveryDraft: true))
                document.IsMissing = true;
        }
        UpdateEditorState();
    }

    public void NotifyFileRenamed(string oldFilePath, string newFilePath)
    {
        var document = FindDocument(oldFilePath);
        if (document is null)
            return;

        CaptureActiveDocument();
        var oldPath = document.FilePath;
        document.UpdateFilePath(NormalizePath(newFilePath));
        if (_unsavedRecoveredPaths.Remove(oldPath))
            _unsavedRecoveredPaths.Add(document.FilePath);
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
            if (_unsavedRecoveredPaths.Remove(oldPath))
                _unsavedRecoveredPaths.Add(document.FilePath);
            if (document.IsDirty)
                MoveRecoveryDraft(oldPath, document);
        }

        SaveEditorSession();
        UpdateEditorState();
    }

    public void CloseAllDocuments()
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

        _discardedDocumentsPendingDraftDeletion.Clear();
        if (_documents.Count == 0)
            ClearEditorSurface();
        SaveEditorSession();
    }

    public void ShowWindow()
    {
        _isHiddenTogether = false;
        _reopenOnStartup = true;
        ShowWindowCore();
        SaveWindowSize();
    }

    internal void RestoreTogether()
    {
        _isHiddenTogether = false;
        ShowWindowCore();
    }

    private void ShowWindowCore()
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
        _isHiddenTogether = false;
        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        SaveEditorSession();
        _reopenOnStartup = false;
        SaveWindowSize();
        Hide();
    }

    internal void HideTogether()
    {
        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        SaveEditorSession();
        _isHiddenTogether = true;
        SaveWindowSize();
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
            var loadResult = _recoveryService.LoadDraft(normalizedPath);
            var issues = loadResult.Issues.ToList();
            var draft = loadResult.Draft;
            var recoveredContent = draft is not null &&
                                   !string.Equals(draft.Content, persistedContent, StringComparison.Ordinal)
                ? draft.Content
                : persistedContent;
            if (draft is not null && !string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
                _unsavedRecoveredPaths.Add(normalizedPath);
            if (draft is not null && string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
                AddRecoveryCleanupIssue(normalizedPath, issues);

            var document = new OpenNoteDocument(
                normalizedPath,
                recoveredContent,
                persistedContent,
                isDirty: !string.Equals(recoveredContent, persistedContent, StringComparison.Ordinal));
            _documents.Add(document);
            ActivateDocument(document);
            SaveEditorSession();
            ShowWindow();
            ShowRecoveryIssues(issues, []);
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
        ICollection<NoteRecoveryIssue> issues,
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
                AddRecoveryCleanupIssue(normalizedPath, issues);

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
        ICollection<NoteRecoveryIssue> issues,
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
                AddRecoveryCleanupIssue(draft.FilePath, issues);
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
        TryFlushRecoveryDraft(out _);
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
            _discardedDocumentsPendingDraftDeletion.Remove(document);
            var result = ShowSavePrompt(document.DisplayName);
            if (result == MessageBoxResult.Cancel)
                return false;
            if (result == MessageBoxResult.Yes && !TrySaveDocument(document))
                return false;
            if (result == MessageBoxResult.No)
                _discardedDocumentsPendingDraftDeletion.Add(document);
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
            var persistedContent = _contentService.Load(document.FilePath);
            if (!string.Equals(
                    persistedContent,
                    document.SavedContent,
                    StringComparison.Ordinal))
            {
                if (!string.Equals(
                        persistedContent,
                        document.Content,
                        StringComparison.Ordinal))
                {
                    ShowError(
                        "Note changed outside Noted",
                        $"'{document.DisplayName}' changed on disk after it was opened. " +
                        "Noted did not overwrite the newer file. Copy your edits if needed, " +
                        "then close and reopen the tab to load the current file.");
                    return false;
                }

                _noteSaveWarning = null;
            }
            else
            {
                _noteSaveWarning = _contentService.Save(
                    document.FilePath,
                    document.Content);
            }

            document.SavedContent = document.Content;
            document.IsDirty = false;
            _unsavedRecoveredPaths.Remove(document.FilePath);
            _discardedDocumentsPendingDraftDeletion.Remove(document);
            RefreshScheduledRecoveryDrafts();
            if (!TryDeleteRecoveryDraft(document.FilePath, out var cleanupError))
                ShowRecoveryCleanupWarning(cleanupError!);
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

    private bool RemoveDocument(
        OpenNoteDocument document,
        bool deleteRecoveryDraft,
        bool saveSession = true)
    {
        var index = _documents.IndexOf(document);
        if (deleteRecoveryDraft &&
            !TryDeleteRecoveryDraft(document.FilePath, out var cleanupError))
        {
            ShowRecoveryCleanupWarning(cleanupError!);
            if (document.IsDirty)
                return false;
        }

        _discardedDocumentsPendingDraftDeletion.Remove(document);
        _unsavedRecoveredPaths.Remove(document.FilePath);
        _documents.Remove(document);
        RefreshScheduledRecoveryDrafts();

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
        return true;
    }

    private void ClearEditorSurface()
    {
        RefreshScheduledRecoveryDrafts();
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
            ? AppDialog.Show(this, message, "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning)
            : AppDialog.Show(message, "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
    }

    private void ShowError(string title, string message)
    {
        if (IsVisible)
            AppDialog.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else
            AppDialog.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
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
        RefreshScheduledRecoveryDrafts();
        if (_activeDocument is { IsDirty: false } document)
            TryDeleteRecoveryDraft(document.FilePath, out _);
    }

    private void RefreshScheduledRecoveryDrafts()
    {
        var snapshots = _documents
            .Where(document => document.IsDirty)
            .Select(document => new RecoveryDraftSnapshot(document.FilePath, document.Content))
            .OrderBy(snapshot => snapshot.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (snapshots.Count == 0)
        {
            _recoverySaveScheduler.CancelPending();
            return;
        }

        _recoverySaveScheduler.Schedule(snapshots);
    }

    private bool TryFlushRecoveryDraft(out string? error)
    {
        CaptureActiveDocument();
        RefreshScheduledRecoveryDrafts();
        return _recoverySaveScheduler.TryFlush(out error);
    }

    private PersistenceSaveResult SaveRecoveryDraftSnapshots(
        IReadOnlyList<RecoveryDraftSnapshot> snapshots)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        foreach (var snapshot in snapshots)
        {
            try
            {
                var warning = _recoveryService.SaveDraft(snapshot.FilePath, snapshot.Content);
                if (!string.IsNullOrWhiteSpace(warning))
                    warnings.Add($"'{Path.GetFileName(snapshot.FilePath)}': {warning}");
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                System.Diagnostics.Debug.WriteLine(ex);
                failures.Add($"'{Path.GetFileName(snapshot.FilePath)}': {ex.Message}");
            }
        }

        var warningMessage = warnings.Count == 0
            ? null
            : $"Some note recovery backups could not be updated. {string.Join(" ", warnings)}";
        return failures.Count == 0
            ? PersistenceSaveResult.Succeeded(warningMessage)
            : PersistenceSaveResult.Failed(
                $"Unsaved note recovery data could not be saved. {string.Join(" ", failures)}",
                warningMessage);
    }

    private void MoveRecoveryDraft(string oldFilePath, OpenNoteDocument document)
    {
        RefreshScheduledRecoveryDrafts();
        string? warning;
        try
        {
            warning = _recoveryService.SaveDraft(document.FilePath, document.Content);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _recoveryOperationError =
                $"Recovery data for '{document.DisplayName}' could not be saved after the note was renamed: {ex.Message}";
            UpdatePersistenceErrorState();
            return;
        }

        if (TryDeleteRecoveryDraft(oldFilePath, out _))
        {
            _recoveryOperationError = warning;
            UpdatePersistenceErrorState();
        }
    }

    private bool SaveEditorSession()
    {
        if (_suppressSessionSave)
            return true;

        CaptureActiveDocument();
        try
        {
            var warning = _sessionService.Save(new NoteEditorSession
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
            _sessionPersistenceError = warning;
            UpdatePersistenceErrorState();
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            _sessionPersistenceError = $"The note editor session could not be saved: {ex.Message}";
            UpdatePersistenceErrorState();
            return false;
        }
    }

    private bool TryDeleteDiscardedRecoveryDrafts()
    {
        foreach (var document in _discardedDocumentsPendingDraftDeletion.ToList())
        {
            if (TryDeleteRecoveryDraft(document.FilePath, out var error))
                continue;

            RefreshScheduledRecoveryDrafts();
            _recoverySaveScheduler.TryFlush(out _);
            ShowRecoveryCleanupWarning(error!);
            return false;
        }

        _discardedDocumentsPendingDraftDeletion.Clear();
        _recoverySaveScheduler.CancelPending();
        return true;
    }

    private bool TryDeleteRecoveryDraft(string filePath, out string? error)
    {
        try
        {
            _recoveryService.DeleteDraft(filePath);
            _recoveryOperationError = null;
            error = null;
            UpdatePersistenceErrorState();
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            error = $"Recovery data for '{Path.GetFileName(filePath)}' could not be removed: {ex.Message}";
            _recoveryOperationError = error;
            UpdatePersistenceErrorState();
            return false;
        }
    }

    private void AddRecoveryCleanupIssue(
        string filePath,
        ICollection<NoteRecoveryIssue> issues)
    {
        if (!TryDeleteRecoveryDraft(filePath, out var error))
            issues.Add(new NoteRecoveryIssue(filePath, error!));
    }

    private void RecoverySaveScheduler_StateChanged(object? sender, EventArgs e)
    {
        UpdatePersistenceErrorState();
    }

    private void UpdatePersistenceErrorState()
    {
        var errors = GetPersistenceIssues();
        PersistenceErrorText.Text = string.Join("\n", errors);
        PersistenceErrorPanel.Visibility = errors.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private IReadOnlyList<string> GetPersistenceIssues() =>
        new[]
            {
                _recoverySaveScheduler.LastError,
                _recoverySaveScheduler.LastWarning,
                _recoveryOperationError,
                _sessionPersistenceError,
                _noteSaveWarning
            }
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Select(error => error!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private void ShowRecoveryCleanupWarning(string message)
    {
        var fullMessage =
            $"Noted could not remove note recovery data. The affected tab or recovery copy was kept so the unsaved text was not silently discarded.\n\n{message}";
        if (IsVisible)
            AppDialog.Show(this, fullMessage, "Recovery Cleanup Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppDialog.Show(fullMessage, "Recovery Cleanup Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowRecoveryIssues(
        IReadOnlyCollection<NoteRecoveryIssue> recoveryIssues,
        IReadOnlyCollection<NoteEditorSessionIssue> sessionIssues)
    {
        var issues = recoveryIssues
            .Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}")
            .Concat(sessionIssues.Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (issues.Count == 0)
            return;

        _recoveryIssuesFoundThisRun = true;

        const int maximumShownIssues = 5;
        var issueText = string.Join("\n", issues.Take(maximumShownIssues));
        var remainingCount = issues.Count - maximumShownIssues;
        var remainingText = remainingCount > 0
            ? $"\n\n{remainingCount} additional recovery issue(s) were not shown."
            : string.Empty;
        var message =
            $"Some note editor recovery files need attention. No recoverable content was silently discarded.\n\n{issueText}{remainingText}";

        if (IsVisible)
            AppDialog.Show(this, message, "Note Editor Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            AppDialog.Show(message, "Note Editor Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                IsTabsPanelCollapsed = _isTabsPanelCollapsed,
                WordWrapEnabled = EditorTextBox.TextWrapping == TextWrapping.Wrap,
                ReopenOnStartup = _reopenOnStartup
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

    private void DeleteActiveNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDocument is not null)
            RequestNoteDeletion(_activeDocument);
    }

    private void DeleteTabNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OpenNoteDocument document })
            RequestNoteDeletion(document);
    }

    private void RequestNoteDeletion(OpenNoteDocument document)
    {
        DeleteNoteRequested?.Invoke(this, new NoteDeleteRequestedEventArgs(document.FilePath));
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
        SetWordWrapEnabled(sender is MenuItem { IsChecked: true });
    }

    private void SetWordWrapEnabled(bool enabled)
    {
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

    private void ReopenTabsOnStartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = sender is MenuItem { IsChecked: true };
        try
        {
            _settingsService.SaveReopenEditorTabsOnStartup(enabled);
            if (enabled)
                SaveEditorSession();
        }
        catch (SettingsPersistenceException ex)
        {
            ReopenTabsOnStartupMenuItem.IsChecked = !enabled;
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
        var screens = DisplayService.GetDisplays();
        var preferredDeviceName = _settingsService.LoadPreferredDisplayDeviceName();
        var screen = !string.IsNullOrWhiteSpace(preferredDeviceName)
            ? screens.FirstOrDefault(screenToCheck =>
                string.Equals(
                    screenToCheck.DeviceName,
                    preferredDeviceName,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        screen ??= screens.FirstOrDefault(screenToCheck => screenToCheck.IsPrimary)
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
        if (_isPreparedForApplicationClose)
        {
            SaveWindowSize();
            return;
        }

        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        SaveEditorSession();
        _reopenOnStartup = false;
        SaveWindowSize();

        e.Cancel = true;
        Hide();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _recoverySaveScheduler.StateChanged -= RecoverySaveScheduler_StateChanged;
        _recoverySaveScheduler.Dispose();
        Closed -= Window_Closed;
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

    private sealed record RecoveryDraftSnapshot(string FilePath, string Content);
}
