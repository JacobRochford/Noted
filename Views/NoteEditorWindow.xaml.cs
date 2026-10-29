using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Noted.Helpers;
using Noted.Models;
using Noted.Search;
using Noted.Services;

namespace Noted;

public partial class NoteEditorWindow : Window
{
    private const double DefaultTabsPanelWidth = 190;
    private const double MinimumTabsPanelWidth = 88;
    private const double MaximumTabsPanelWidth = 360;
    private const double TabsPanelCollapseThreshold = 36;
    internal static RoutedUICommand ReopenClosedTabCommand { get; } = new(
        "Reopen closed note", nameof(ReopenClosedTabCommand), typeof(NoteEditorWindow));

    private readonly INoteContentService _contentService;
    private readonly IAppSettingsService _settingsService;
    private readonly INoteFileService? _noteFiles;
    private readonly INoteRecoveryService _recoveryService;
    private readonly NoteEditorPersistence _persistence;
    private readonly SaveScheduler<IReadOnlyList<RecoveryDraftSnapshot>> _recoverySaveScheduler;
    private readonly NoteEditorWorkspace _workspace = new();
    private readonly HashSet<OpenNoteDocument> _discardedDocumentsPendingDraftDeletion = [];
    private readonly HashSet<string> _unsavedRecoveredPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _documentTimeText =
        new(StringComparer.OrdinalIgnoreCase);
    private OpenNoteDocument? _draggedDocument;
    private OpenNoteDocument? _primaryBeforeTabDrag;
    private OpenNoteDocument? _middlePressedDocument;
    private Point _tabDragStart;
    private bool _isLoading;
    private bool _isLoadingSecondary;
    private bool _isSelectingTab;
    private Guid? _lastSecondaryDocumentId;
    private bool _hasLoaded;
    private readonly MarkdownPreviewPresenter _primaryPreview;
    private readonly MarkdownPreviewPresenter _secondaryPreview;
    private readonly List<(MenuItem Menu, Func<OpenNoteDocument?> Document)> _previewMenus = [];
    private int _scrollSpeed = 2;
    private bool _isPreparedForApplicationClose;
    private double _tabsPanelWidth = DefaultTabsPanelWidth;
    private bool _isTabsPanelCollapsed;
    private bool _reopenOnStartup;
    private bool _isHiddenTogether;
    private WindowState _restoreWindowState = WindowState.Normal;
    private bool _suppressSessionSave;
    private bool _recoveryIssuesFoundThisRun;
    private string? _recoveryOperationError;
    private string? _sessionPersistenceError;
    private string? _noteSaveWarning;
    private readonly TextSearchSession _findSession = new();
    private readonly TextBoxSearchPresenter _primaryFindPresenter;
    private readonly TextBoxSearchPresenter _secondaryFindPresenter;
    private TextBoxSearchPresenter? _activeFindPresenter;
    private TextBox? _activeFindEditor;
    private OpenNoteDocument? _activeFindDocument;
    private bool _isApplyingFindReplacement;
    private int _findRefreshVersion;
    private bool _isSeedingFindQuery;
    private ReadOnlyObservableCollection<OpenNoteDocument> _documents => _workspace.Documents;
    private OpenNoteDocument? _activeDocument => _workspace.ActiveDocument;
    private OpenNoteDocument? _secondaryDocument => _workspace.SecondaryDocument;
    private OpenNoteDocument? _focusedDocument => _workspace.FocusedDocument;

    public NoteEditorWindow(
        INoteContentService contentService,
        IAppSettingsService settingsService,
        INoteRecoveryService recoveryService,
        INoteEditorSessionService sessionService,
        INoteFileService? noteFiles = null)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(sessionService);

        _contentService = contentService;
        _settingsService = settingsService;
        _noteFiles = noteFiles;
        _recoveryService = recoveryService;
        _persistence = new NoteEditorPersistence(contentService, recoveryService, sessionService, settingsService.LoadDefaultIndentation);
        _workspace.DocumentPathsChanged += Workspace_DocumentPathsChanged;

        InitializeComponent();
        AttachReopenShortcut(this);
        _primaryPreview = new MarkdownPreviewPresenter(EditorTextBox, PrimaryTextEditor, MarkdownPreview, PrimaryPreviewLayout);
        _secondaryPreview = new MarkdownPreviewPresenter(SecondaryEditorTextBox, SecondaryTextEditor, SecondaryMarkdownPreview, SecondaryPreviewLayout);
        AddMarkdownPreviewMenu(HeaderMarkdownMenuItem, () => CurrentDocument);
        AddMarkdownPreviewMenu(ContextMarkdownMenuItem, () => _activeDocument);
        AddMarkdownPreviewMenu(PreviewMarkdownMenuItem, () => _activeDocument);
        var secondaryMenu = new MenuItem { Header = "Markdown Preview" };
        SecondaryEditorTextBox.ContextMenu.Items.Add(secondaryMenu);
        AddMarkdownPreviewMenu(secondaryMenu, () => _secondaryDocument);
        SecondaryMarkdownPreview.ContextMenu = new ContextMenu { Style = EditorTextBox.ContextMenu.Style };
        var secondaryPreviewMenu = new MenuItem { Header = "Markdown Preview" };
        SecondaryMarkdownPreview.ContextMenu.Items.Add(secondaryPreviewMenu);
        AddMarkdownPreviewMenu(secondaryPreviewMenu, () => _secondaryDocument);
        MarkdownPreview.GotKeyboardFocus += (_, _) => { _workspace.Focus(_activeDocument); UpdateEditorState(); };
        SecondaryMarkdownPreview.GotKeyboardFocus += (_, _) => { _workspace.Focus(_secondaryDocument); UpdateEditorState(); };
        WindowManager.ConfigureEditorActivation(this);
        EditorIndentation.Attach(EditorTextBox, () => _activeDocument?.Indentation);
        EditorTextWrapping.Attach(EditorTextBox);
        EditorIndentation.Attach(SecondaryEditorTextBox, () => _secondaryDocument?.Indentation);
        EditorTextWrapping.Attach(SecondaryEditorTextBox);
        EditorJsonFormatting.Attach(EditorTextBox, () => EditorTextBox.IsVisible ? _activeDocument?.Indentation : null);
        EditorJsonFormatting.Attach(SecondaryEditorTextBox, () => SecondaryEditorTextBox.IsVisible ? _secondaryDocument?.Indentation : null);
        EditorJsonFormatting.AddMenuItems(EditMenu, () => CurrentEditorTextBox);
        EditorTextTransformations.Attach(EditorTextBox);
        EditorTextTransformations.Attach(SecondaryEditorTextBox);
        EditorTextTransformations.AddMenuItems(EditMenu, () => CurrentEditorTextBox);
        EditorNavigation.Attach(EditorTextBox, () => SetWordWrapEnabled(EditorTextBox.TextWrapping != TextWrapping.Wrap));
        EditorNavigation.Attach(SecondaryEditorTextBox, () => SetWordWrapEnabled(SecondaryEditorTextBox.TextWrapping != TextWrapping.Wrap));
        EditorNavigation.AddMenuItem(EditMenu, () => CurrentEditorTextBox);
        EditMenu.SubmenuOpened += (_, _) =>
        {
            foreach (var item in EditMenu.Items.OfType<MenuItem>())
                if (item.Command is RoutedCommand command && command.OwnerType == typeof(ApplicationCommands))
                    item.CommandTarget = CurrentEditorTextBox;
        };
        EditorIndentation.AttachIndicator(IndentationButton, () => CurrentDocument?.Indentation, value =>
        {
            if (CurrentDocument is not { } document) return;
            document.Indentation = value;
            SaveEditorSession();
            UpdateDocumentFooter();
        }, () => CurrentEditorTextBox);
        PlainTextPaste.Attach(EditorTextBox, _settingsService.LoadNormalizePastedText);
        PlainTextPaste.Attach(SecondaryEditorTextBox, _settingsService.LoadNormalizePastedText);
        EditorLineCommands.Attach(EditorTextBox);
        EditorLineCommands.Attach(SecondaryEditorTextBox);
        _primaryFindPresenter = new TextBoxSearchPresenter(EditorTextBox);
        _secondaryFindPresenter = new TextBoxSearchPresenter(SecondaryEditorTextBox);
        _findSession.QueryOrOptionsChanged += _primaryFindPresenter.ClearPendingManualNavigationAnchor;
        _findSession.QueryOrOptionsChanged += _secondaryFindPresenter.ClearPendingManualNavigationAnchor;
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
        SetLineNumbers(windowState.ShowLineNumbers);
        SetScrollSpeed(windowState.ScrollSpeed);
        AlwaysVisibleMenuItem.IsChecked = settingsService.LoadWindowAlwaysVisible(PersistenceKeys.NoteEditorWindow);
        ReopenTabsOnStartupMenuItem.IsChecked = _settingsService.LoadReopenEditorTabsOnStartup();
        FileExplorerIntegrationMenuItem.IsChecked = FileExplorerIntegrationService.IsEnabled();
        UpdateEditorState();
    }

    public string? OpenFilePath => CurrentDocument?.FilePath;
    public bool IsDirty => _workspace.IsDirty;
    public bool IsWindowVisible => IsVisible && WindowState != WindowState.Minimized;
    internal bool CanChangeGroupVisibility
    {
        get
        {
            // Modal dialogs disable the native owner, including native file dialogs.
            var handle = new WindowInteropHelper(this).Handle;
            return handle == IntPtr.Zero || WindowInterop.IsWindowEnabled(handle);
        }
    }
    internal bool RecoveryBlocksBackup =>
        _unsavedRecoveredPaths.Count > 0 || _recoveryIssuesFoundThisRun;

    internal string? BackupBlockingIssue => GetPersistenceIssues().FirstOrDefault();
    public event EventHandler? NewNoteRequested;
    public event EventHandler<NoteDeleteRequestedEventArgs>? DeleteNoteRequested;
    public event EventHandler<NoteRenameRequestedEventArgs>? NoteRenameRequested;
    internal NoteEditorWorkspace Workspace => _workspace;

    private OpenNoteDocument? CurrentDocument => _workspace.CurrentDocument;
    private TextBox CurrentEditorTextBox => ReferenceEquals(CurrentDocument, _secondaryDocument)
        ? SecondaryEditorTextBox
        : EditorTextBox;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AppThemeManager.ApplyTitleBar(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _restoreWindowState = WindowState;
            _isHiddenTogether = false;
        }
        base.OnStateChanged(e);
    }

    public bool OpenNote(string filePath)
    {
        return OpenNoteCore(filePath, usesGeneratedName: false);
    }

    private void OpenNoteMenuItem_Click(object sender, RoutedEventArgs e) => OpenNoteFromDialog();

    internal bool OpenNoteFromDialog(Func<OpenFileDialog, bool?>? showDialog = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Note",
            Filter = NoteFileExtensions.FileDialogFilter,
            InitialDirectory = _noteFiles?.CurrentDirectory ?? _settingsService.LoadNotesDirectory(),
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };
        var accepted = showDialog is null ? dialog.ShowDialog(this) : showDialog(dialog);
        return accepted == true && OpenNote(dialog.FileName);
    }

    private void QuickOpenMenuItem_Click(object sender, RoutedEventArgs e) => OpenQuickNote();

    internal IReadOnlyList<QuickOpenNote> GetQuickOpenNotes()
    {
        if (_noteFiles is null) return [];
        return _noteFiles.GetAllNoteKeys().Select(key =>
        {
            var path = Path.GetFullPath(Path.Combine(_noteFiles.NotesDirectory, key));
            return new QuickOpenNote(path, Path.GetRelativePath(_noteFiles.NotesDirectory, path));
        }).ToList();
    }

    internal bool OpenQuickNote(Func<QuickOpenDialog, bool?>? showDialog = null)
    {
        try
        {
            var dialog = new QuickOpenDialog(GetQuickOpenNotes()) { Owner = this };
            var accepted = showDialog is null ? dialog.ShowDialog() : showDialog(dialog);
            if (accepted == true && dialog.SelectedFilePath is { } path) return OpenNote(path);
            FocusActiveSurface();
            return false;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to list notes", "The note list could not be read. Check the notes folder and access permissions.");
            return false;
        }
    }

    internal void AttachReopenShortcut(Window window, Func<bool>? isContextEnabled = null)
    {
        window.InputBindings.Add(new KeyBinding(ReopenClosedTabCommand,
            Key.T, ModifierKeys.Control | ModifierKeys.Shift));
        window.CommandBindings.Add(new CommandBinding(ReopenClosedTabCommand,
            (_, e) => { ReopenClosedTab(); e.Handled = true; },
            (_, e) =>
            {
                e.CanExecute = !_isPreparedForApplicationClose && CanChangeGroupVisibility &&
                    _workspace.ClosedTabCount > 0 && (isContextEnabled?.Invoke() ?? true);
                e.Handled = true;
            }));
    }

    internal bool ReopenClosedTab()
    {
        if (_isPreparedForApplicationClose)
            return false;
        return _workspace.ReopenClosedTab(tab =>
        {
            if (!OpenNoteCore(tab.State.FilePath, tab.State.UsesGeneratedName, reopenState: tab.State))
                return false;

            var document = FindDocument(tab.State.FilePath)!;
            var primary = tab.PrimaryPath is null ? null : FindDocument(tab.PrimaryPath);
            var secondary = tab.SecondaryPath is null ? null : FindDocument(tab.SecondaryPath);
            if (primary is not null && secondary is not null &&
                (ReferenceEquals(document, primary) || ReferenceEquals(document, secondary)))
            {
                CloseSecondaryPane(saveSession: false);
                ActivateDocument(primary, focusEditor: false);
                ShowSecondaryDocument(secondary, focusPane: ReferenceEquals(document, secondary),
                    focusEditor: ReferenceEquals(document, secondary), saveSession: true);
                if (ReferenceEquals(document, primary))
                    FocusActiveSurface();
            }
            return true;
        });
    }

    internal bool OpenCreatedNote(string filePath, bool usesGeneratedName, string initialContent)
    {
        return OpenNoteCore(filePath, usesGeneratedName, initialContent);
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

        _reopenOnStartup = reopenOnStartup;
        SaveWindowSize();
        _isPreparedForApplicationClose = true;
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
        var loadResult = _persistence.LoadWorkspace(
            _settingsService.LoadReopenEditorTabsOnStartup());

        _suppressSessionSave = true;
        try
        {
            foreach (var document in loadResult.Documents)
                _workspace.Add(document);

            if (_documents.Count > 0)
            {
                var activeDocument = loadResult.ActiveDocumentId.HasValue
                    ? _workspace.FindById(loadResult.ActiveDocumentId.Value) ?? _documents[0]
                    : _documents[0];
                ActivateDocument(activeDocument);

                var secondaryDocument = loadResult.SecondaryDocumentId.HasValue
                    ? _workspace.FindById(loadResult.SecondaryDocumentId.Value)
                    : null;
                if (secondaryDocument is not null)
                    ShowSecondaryDocument(
                        secondaryDocument,
                        focusPane: false,
                        focusEditor: false,
                        saveSession: false);
            }
        }
        finally
        {
            _suppressSessionSave = false;
        }

        if (_documents.Count > 0)
            SaveEditorSession();

        _recoveryOperationError = loadResult.RecoveryOperationError;
        UpdatePersistenceErrorState();
        foreach (var document in _documents.Where(document => document.IsDirty))
            _unsavedRecoveredPaths.Add(document.FilePath);

        ShowRecoveryIssues(loadResult.RecoveryIssues, loadResult.SessionIssues);
        if (_documents.Count > 0 &&
            (_reopenOnStartup ||
             _unsavedRecoveredPaths.Count > 0 ||
             loadResult.RecoveryIssues.Count > 0 ||
             loadResult.SessionIssues.Count > 0))
        {
            ShowWindow();
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
                _workspace.MarkMissing(document);
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
                _workspace.MarkMissing(document);
        }
        UpdateEditorState();
    }

    private void UpdateDocumentPathAfterRename(OpenNoteDocument document, string newFilePath)
    {
        var normalizedNewPath = NormalizePath(newFilePath);
        _workspace.UpdatePath(
            document,
            normalizedNewPath,
            clearGeneratedName: true,
            markPresent: true);
    }

    private void Workspace_DocumentPathsChanged(
        object? sender,
        NoteEditorDocumentPathsChangedEventArgs e)
    {
        CaptureActiveDocument();
        foreach (var change in e.Changes)
        {
            _documentTimeText.Remove(change.OldFilePath);
            _documentTimeText.Remove(change.NewFilePath);
            if (_unsavedRecoveredPaths.Remove(change.OldFilePath))
                _unsavedRecoveredPaths.Add(change.NewFilePath);
            if (change.Document.IsDirty)
                MoveRecoveryDraft(change.OldFilePath, change.Document);
            if (ReferenceEquals(change.Document, _secondaryDocument))
                SecondaryDocumentTitle.Text = change.Document.TabLabel;
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

    internal void ShowWindow()
    {
        _isHiddenTogether = false;
        _reopenOnStartup = true;
        ShowWindowCore();
        SaveWindowSize();
    }

    internal void RestoreTogether()
    {
        if (!_isHiddenTogether)
            return;

        _isHiddenTogether = false;
        ShowWindowCore();
    }

    private void ShowWindowCore()
    {
        WindowInterop.RestoreWindow(this, _restoreWindowState);

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
        if (!IsWindowVisible || !CanChangeGroupVisibility)
            return;

        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        SaveEditorSession();
        _isHiddenTogether = true;
        SaveWindowSize();
        WindowState = WindowState.Minimized;
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
        {
            _workspace.UpdateViewState(
                _activeDocument,
                EditorTextBox.CaretIndex,
                EditorTextBox.VerticalOffset);
        }
        FocusActiveSurface();
    }

    private bool OpenNoteCore(string filePath, bool usesGeneratedName, string? initialFileContent = null,
        NoteEditorTabState? reopenState = null)
    {
        string normalizedPath;
        try
        {
            normalizedPath = NormalizePath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException || IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to open note", "The note could not be opened. Check the file location and access permissions.");
            return false;
        }

        if (!NoteFileExtensions.IsSupported(normalizedPath))
        {
            ShowError("Unable to open note", "Only supported text-based files can be opened.");
            return false;
        }

        var existingDocument = FindDocument(normalizedPath);
        if (existingDocument is not null)
        {
            _workspace.UpdateCreationMetadata(existingDocument, usesGeneratedName, initialFileContent);
            ActivateDocument(existingDocument);
            if (usesGeneratedName || initialFileContent is not null)
                SaveEditorSession();
            ShowWindow();
            return true;
        }

        try
        {
            var loadedFile = _contentService.LoadWithEncoding(normalizedPath);
            var persistedContent = loadedFile.Content;
            // Reopening an explicitly closed tab always reads disk, respecting the close decision.
            var loadResult = reopenState is null ? _recoveryService.LoadDraft(normalizedPath) : null;
            var issues = loadResult?.Issues.ToList() ?? [];
            var draft = loadResult?.Draft;
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
                isDirty: !string.Equals(recoveredContent, persistedContent, StringComparison.Ordinal),
                usesGeneratedName: usesGeneratedName,
                initialFileContent: initialFileContent,
                caretIndex: Math.Clamp(reopenState?.CaretIndex ?? 0, 0, recoveredContent.Length),
                verticalOffset: reopenState?.VerticalOffset ?? 0,
                indentation: reopenState?.Indentation ?? _settingsService.LoadDefaultIndentation(),
                encoding: loadedFile.Encoding);
            if (reopenState is not null)
                document.MarkdownPreviewMode = reopenState.MarkdownPreviewMode;
            _workspace.Add(document);
            OpenDocumentInFocusedPane(document, focusEditor: true, saveSession: true);
            ShowWindow();
            ShowRecoveryIssues(issues, []);
            return true;
        }
        catch (TextEncodingException ex)
        {
            ShowError("Unable to open note", ex.Message);
            return false;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to open note", "The note could not be opened. Check the file location and access permissions.");
            return false;
        }
    }

    private void ActivateDocument(OpenNoteDocument document, bool focusEditor = true)
    {
        if (ReferenceEquals(_secondaryDocument, document))
        {
            _workspace.Activate(document);
            SelectTab(document);
            UpdateEditorState();
            if (focusEditor)
                FocusActiveSurface();
            return;
        }

        if (ReferenceEquals(_activeDocument, document))
        {
            _workspace.Activate(document);
            SelectTab(document);
            UpdateEditorState();
            if (focusEditor)
                FocusActiveSurface();
            return;
        }

        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        _workspace.Activate(document);

        LoadActiveDocument(document, focusEditor);
    }

    private void LoadActiveDocument(OpenNoteDocument document, bool focusEditor = true)
    {
        _isLoading = true;
        EditorTextBox.Text = document.Content;
        EditorTextBox.CaretIndex = Math.Clamp(document.CaretIndex, 0, document.Content.Length);
        EditorTextBox.ScrollToVerticalOffset(Math.Max(0, document.VerticalOffset));
        ResetUndoHistory();
        _isLoading = false;

        SelectTab(document);
        _primaryPreview.SetMode(EffectivePreviewMode(document));
        UpdateEditorState();
        SaveEditorSession();
        if (FindPanel.Visibility == Visibility.Visible)
            RefreshFindResults(revealCurrent: true);
        if (focusEditor)
            FocusActiveSurface();
    }

    private void CaptureActiveDocument()
    {
        if (!_isLoading && _activeDocument is not null)
        {
            _workspace.UpdateContent(
                _activeDocument,
                EditorTextBox.Text,
                EditorTextBox.CaretIndex);
            _workspace.UpdateViewState(
                _activeDocument,
                EditorTextBox.CaretIndex,
                EditorTextBox.VerticalOffset);
        }

        if (!_isLoadingSecondary && _secondaryDocument is not null)
        {
            _workspace.UpdateContent(
                _secondaryDocument,
                SecondaryEditorTextBox.Text,
                SecondaryEditorTextBox.CaretIndex);
            _workspace.UpdateViewState(
                _secondaryDocument,
                SecondaryEditorTextBox.CaretIndex,
                SecondaryEditorTextBox.VerticalOffset);
        }
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
        return CurrentDocument is not { } document || TrySaveDocument(document);
    }

    private bool TrySaveDocument(OpenNoteDocument document)
    {
        if (ReferenceEquals(document, _activeDocument) || ReferenceEquals(document, _secondaryDocument))
            CaptureActiveDocument();

        if (document.IsMissing || !File.Exists(document.FilePath))
        {
            _workspace.MarkMissing(document);
            UpdateEditorState();
            ShowError(
                "Original note is missing",
                "The original file no longer exists. Its recovered text has not been discarded. " +
                "Copy the text into a new note before closing this tab.");
            return false;
        }

        try
        {
            var loadedFile = _contentService.LoadWithEncoding(document.FilePath);
            var persistedContent = loadedFile.Content;
            var shouldWriteContent = loadedFile.Encoding == document.SavedEncoding && string.Equals(
                persistedContent,
                document.SavedContent,
                StringComparison.Ordinal);
            if (!shouldWriteContent)
            {
                if (loadedFile.Encoding != document.Encoding || !string.Equals(
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

            if (document.UsesGeneratedName && !TryRenameNote(document, isFirstSave: true))
                return false;

            if (shouldWriteContent)
            {
                _noteSaveWarning = _contentService.Save(
                    document.FilePath,
                    document.Content,
                    document.Encoding,
                    new TextFileContent(document.SavedContent, document.SavedEncoding));
            }

            _workspace.MarkSaved(document);
            _documentTimeText.Remove(document.FilePath);
            if (ReferenceEquals(document, _secondaryDocument))
                SecondaryDocumentTitle.Text = document.TabLabel;
            _unsavedRecoveredPaths.Remove(document.FilePath);
            _discardedDocumentsPendingDraftDeletion.Remove(document);
            RefreshScheduledRecoveryDrafts();
            if (!TryDeleteRecoveryDraft(document.FilePath, out var cleanupError))
                ShowRecoveryCleanupWarning(cleanupError!);
            SaveEditorSession();
            if (ReferenceEquals(document, CurrentDocument))
                UpdateEditorState();
            return true;
        }
        catch (NoteSaveConflictException ex)
        {
            _noteSaveWarning = ex.Message;
            ShowError("Note changed outside Noted", ex.Message);
            return false;
        }
        catch (FileVerificationException ex)
        {
            _noteSaveWarning = ex.Message;
            ShowError("Unable to verify saved note", ex.Message);
            return false;
        }
        catch (TextEncodingException ex)
        {
            ShowError("Unable to save note", ex.Message);
            return false;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to save note", "The note could not be saved. Check file access and available disk space.");
            return false;
        }
    }

    private bool TryRenameNote(OpenNoteDocument document, bool isFirstSave)
    {
        if (NoteRenameRequested is null)
        {
            ShowError(
                "Unable to name note",
                "Noted could not open the note naming prompt. The note was not saved.");
            return false;
        }

        var request = new NoteRenameRequestedEventArgs(document.FilePath, isFirstSave);
        NoteRenameRequested.Invoke(this, request);
        if (request.IsCanceled)
            return false;
        if (string.IsNullOrWhiteSpace(request.NewFilePath))
        {
            ShowError(
                "Unable to name note",
                "The note was not renamed, so Noted did not save it.");
            return false;
        }

        UpdateDocumentPathAfterRename(document, request.NewFilePath);
        return true;
    }

    private bool RemoveDocument(
        OpenNoteDocument document,
        bool deleteRecoveryDraft,
        bool saveSession = true,
        bool rememberClosed = false)
    {
        CaptureActiveDocument();
        if (deleteRecoveryDraft &&
            !TryDeleteRecoveryDraft(document.FilePath, out var cleanupError))
        {
            ShowRecoveryCleanupWarning(cleanupError!);
            if (document.IsDirty)
                return false;
        }

        _discardedDocumentsPendingDraftDeletion.Remove(document);
        _unsavedRecoveredPaths.Remove(document.FilePath);
        _documentTimeText.Remove(document.FilePath);
        var closeResult = _workspace.Close(document, rememberClosed && !_isPreparedForApplicationClose);
        if (closeResult.SecondaryDocumentClosed)
        {
            ClearSecondaryPaneSurface();
            UpdateEditorState();
        }
        RefreshScheduledRecoveryDrafts();

        if (closeResult.ActiveDocumentChanged)
        {
            if (closeResult.ActiveDocument is not null)
            {
                LoadActiveDocument(closeResult.ActiveDocument);
            }
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
        CloseSecondaryPane(saveSession: false);
        CloseFind(focusEditor: false);
        RefreshScheduledRecoveryDrafts();
        _isLoading = true;
        EditorTextBox.Clear();
        ResetUndoHistory();
        _isLoading = false;
        SelectTab(null);
        SetMarkdownPreviewEnabled(false, saveSession: false);
        UpdateEditorState();
    }

    private OpenNoteDocument? FindDocument(string filePath)
    {
        try
        {
            var normalizedPath = NormalizePath(filePath);
            return _workspace.FindByPath(normalizedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException || IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return null;
        }
    }

    private void ResetUndoHistory()
    {
        EditorTextBox.IsUndoEnabled = false;
        EditorTextBox.IsUndoEnabled = true;
    }

    private void ResetSecondaryUndoHistory()
    {
        SecondaryEditorTextBox.IsUndoEnabled = false;
        SecondaryEditorTextBox.IsUndoEnabled = true;
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
        var currentDocument = CurrentDocument;
        SelectTab(currentDocument);
        PrimaryDocumentHeader.SetResourceReference(Border.BackgroundProperty,
            ReferenceEquals(currentDocument, _activeDocument) ? "NotedAccentSoftBrush" : "NotedSubtleSurfaceBrush");
        SecondaryDocumentHeader.SetResourceReference(Border.BackgroundProperty,
            ReferenceEquals(currentDocument, _secondaryDocument) ? "NotedAccentSoftBrush" : "NotedSubtleSurfaceBrush");
        PrimaryDocumentHeader.Visibility = _secondaryDocument is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrimaryDocumentTitle.Text = _activeDocument?.TabLabel ?? string.Empty;
        SecondaryDocumentTitle.Text = _secondaryDocument?.TabLabel ?? string.Empty;
        DirtyStatusText.Visibility = currentDocument?.IsDirty == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        DirtyStatusText.Text = currentDocument?.IsMissing == true
            ? "Recovered changes - original file missing"
            : "Unsaved changes";
        EditorTextBox.IsEnabled = hasDocument;
        EditorTextBox.IsReadOnly = _activeDocument?.IsMissing == true;
        SecondaryEditorTextBox.IsEnabled = _secondaryDocument is not null;
        SecondaryEditorTextBox.IsReadOnly = _secondaryDocument?.IsMissing == true;
        FindButton.IsEnabled = currentDocument is not null;
        FindPanel.IsEnabled = currentDocument is not null;
        ReplaceButton.IsEnabled = currentDocument?.IsMissing == false;
        ReplaceAllButton.IsEnabled = currentDocument?.IsMissing == false;
        SaveButton.IsEnabled = currentDocument is { IsMissing: false } document &&
            (document.IsDirty || document.UsesGeneratedName);
        Title = currentDocument is not null
            ? $"{(currentDocument.IsDirty ? "*" : string.Empty)}{currentDocument.DisplayName} - Noted"
            : "Noted";
        UpdateMarkdownPreviewMenus();
        UpdateEncodingMenu();
        UpdateDocumentFooter();
    }

    private void UpdateEncodingMenu()
    {
        var document = CurrentDocument;
        EncodingMenu.IsEnabled = document is not null;
        EncodingMenu.Header = document is null
            ? "Encoding"
            : $"Encoding: {TextFileCodec.GetDisplayName(document.Encoding)}";
        foreach (var item in EncodingMenu.Items.OfType<MenuItem>())
            item.IsChecked = document is not null && item.Tag is string tag &&
                Enum.TryParse<TextFileEncoding>(tag, out var encoding) && document.Encoding == encoding;
    }

    private void EncodingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is not { } document || sender is not MenuItem { Tag: string tag } ||
            !Enum.TryParse<TextFileEncoding>(tag, out var encoding))
            return;
        CaptureActiveDocument();
        _workspace.SetEncoding(document, encoding);
        UpdateEditorState();
        ScheduleRecoveryDraftSave();
        SaveEditorSession();
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
                ExceptionDiagnostics.Record(ex);
                failures.Add($"'{Path.GetFileName(snapshot.FilePath)}': recovery data could not be saved.");
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
            ExceptionDiagnostics.Record(ex);
            _recoveryOperationError =
                $"Recovery data for '{document.DisplayName}' could not be saved after the note was renamed.";
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
        var result = _persistence.SaveSession(_workspace);
        _sessionPersistenceError = result.Success ? result.Warning : result.Error;
        UpdatePersistenceErrorState();
        return result.Success;
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
            ExceptionDiagnostics.Record(ex);
            error = $"Recovery data for '{Path.GetFileName(filePath)}' could not be removed.";
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
        _settingsService.SaveNoteEditorWindowState(new NoteEditorWindowState
        {
            Width = NormalizeWindowDimension(bounds.Width, MinWidth, 900),
            Height = NormalizeWindowDimension(bounds.Height, MinHeight, 650),
            TabsPanelWidth = _tabsPanelWidth,
            IsTabsPanelCollapsed = _isTabsPanelCollapsed,
            WordWrapEnabled = EditorTextBox.TextWrapping == TextWrapping.Wrap,
            ShowLineNumbers = LineNumbersMenuItem.IsChecked,
            ScrollSpeed = _scrollSpeed,
            ReopenOnStartup = _reopenOnStartup
        });
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _isApplyingFindReplacement || _activeDocument is null)
            return;

        StoreEditorChanges();
        UpdateDocumentFooter();
        if (FindPanel.Visibility == Visibility.Visible)
            RefreshFindResults(revealCurrent: false);
    }

    private void EditorTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateDocumentFooter();
    }

    private void EditorTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _workspace.Focus(_activeDocument);
        if (FindPanel.Visibility == Visibility.Visible)
        {
            PositionFindPanel();
            RefreshFindResults(revealCurrent: true);
        }
        UpdateEditorState();
    }

    private void SecondaryEditorTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _workspace.Focus(_secondaryDocument);
        if (FindPanel.Visibility == Visibility.Visible)
        {
            PositionFindPanel();
            RefreshFindResults(revealCurrent: true);
        }
        UpdateEditorState();
    }

    private void SecondaryEditorTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(CurrentDocument, _secondaryDocument))
            UpdateDocumentFooter();
    }

    private void SecondaryEditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSecondary || _isApplyingFindReplacement || _secondaryDocument is null)
            return;

        StoreSecondaryEditorChanges();
        UpdateDocumentFooter();
        if (FindPanel.Visibility == Visibility.Visible)
            RefreshFindResults(revealCurrent: false);
    }

    private void EditorTextBox_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not TextBox editor)
            return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => TrimTrailingDoubleClickWhitespace(editor)));
    }

    private static void TrimTrailingDoubleClickWhitespace(TextBox editor)
    {
        if (editor.SelectionLength <= 0)
            return;

        var selectedText = editor.SelectedText;
        var trimmedLength = selectedText.TrimEnd(' ', '\t').Length;
        if (trimmedLength <= 0 || trimmedLength == selectedText.Length)
            return;

        editor.Select(editor.SelectionStart, trimmedLength);
    }

    private void UpdateDocumentFooter()
    {
        var document = CurrentDocument;
        IndentationButton.IsEnabled = document is not null;
        IndentationButton.Content = document is null ? string.Empty : EditorIndentation.GetLabel(document.Indentation);
        var textBox = ReferenceEquals(document, _secondaryDocument)
            ? SecondaryEditorTextBox
            : EditorTextBox;
        if (document is null)
        {
            DocumentCountText.Text = string.Empty;
            DocumentTimeText.Text = string.Empty;
            DocumentTimeText.ToolTip = null;
            CaretPositionText.Text = string.Empty;
            return;
        }

        var text = textBox.Text;
        var wordCount = string.IsNullOrWhiteSpace(text)
            ? 0
            : Regex.Matches(text, @"\S+").Count;
        var lines = new TextLineMap(text);
        DocumentCountText.Text =
            $"Words {wordCount}   Characters {text.Length}   Lines {lines.Starts.Length}";

        var caretIndex = Math.Clamp(textBox.CaretIndex, 0, text.Length);
        var lineIndex = lines.LineAt(caretIndex);
        var lineStart = lines.Starts[lineIndex];
        CaretPositionText.Text =
            $"Ln {Math.Max(0, lineIndex) + 1}, Col {caretIndex - lineStart + 1}";

        DocumentTimeText.Text = GetDocumentTimeText(document);
        DocumentTimeText.ToolTip = document.FilePath;
    }

    private string GetDocumentTimeText(OpenNoteDocument document)
    {
        if (document.IsMissing)
            return "Original file missing";
        if (_documentTimeText.TryGetValue(document.FilePath, out var text))
            return text;

        try
        {
            var created = File.GetCreationTime(document.FilePath);
            var modified = File.GetLastWriteTime(document.FilePath);
            text = $"Created {created:g}   Modified {modified:g}";
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            text = string.Empty;
        }

        _documentTimeText[document.FilePath] = text;
        return text;
    }

    private void StoreEditorChanges()
    {
        if (_activeDocument is not { } activeDocument)
            return;

        _workspace.UpdateContent(
            activeDocument,
            EditorTextBox.Text,
            EditorTextBox.CaretIndex);
        ScheduleRecoveryDraftSave();
        UpdateEditorState();
    }

    private void StoreCurrentEditorChanges()
    {
        if (!ReferenceEquals(CurrentDocument, _secondaryDocument))
        {
            StoreEditorChanges();
            return;
        }

        StoreSecondaryEditorChanges();
    }

    private void StoreSecondaryEditorChanges()
    {
        if (_secondaryDocument is null)
            return;

        _workspace.UpdateContent(
            _secondaryDocument,
            SecondaryEditorTextBox.Text,
            SecondaryEditorTextBox.CaretIndex);
        SecondaryDocumentTitle.Text = _secondaryDocument.TabLabel;
        ScheduleRecoveryDraftSave();
        UpdateEditorState();
    }

    private void FindButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFind(showReplace: false);
    }

    private void FindMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenFind(showReplace: false);
    }

    private void ReplaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenFind(showReplace: true);
    }

    private void OpenFind(bool showReplace)
    {
        var wasVisible = FindPanel.Visibility == Visibility.Visible;
        var seed = FindSelectionSeed.GetQuery(CurrentEditorTextBox.SelectedText, wasVisible, CurrentEditorTextBox.IsKeyboardFocusWithin);
        if (wasVisible && seed is null)
        {
            if (showReplace)
            {
                ShowReplaceToggle.IsChecked = true;
                UpdateReplaceRowVisibility();
            }
            FindTextBox.Focus();
            FindTextBox.SelectAll();
            return;
        }

        var targetDocument = CurrentDocument;
        if (targetDocument is null)
            return;

        if (CurrentPreview.Mode == MarkdownPreviewMode.Preview)
        {
            SetMarkdownPreviewMode(targetDocument, MarkdownPreviewMode.Off);
            if (ReferenceEquals(targetDocument, _secondaryDocument))
            {
                _workspace.Focus(targetDocument);
                SecondaryEditorTextBox.Focus();
            }
        }

        PositionFindPanel();
        var presenter = ActivateFindPresenter();
        if (wasVisible)
        {
            _findRefreshVersion++;
            _findSession.Reset();
            presenter.BeginFind();
        }
        if (showReplace || !wasVisible)
            ShowReplaceToggle.IsChecked = showReplace;
        UpdateReplaceRowVisibility();

        if (seed is not null)
        {
            _isSeedingFindQuery = true;
            try { FindTextBox.Text = seed; }
            finally { _isSeedingFindQuery = false; }
        }
        FindPanel.Visibility = Visibility.Visible;
        RefreshFindResults(revealCurrent: false);

        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void CloseFindButton_Click(object sender, RoutedEventArgs e)
    {
        CloseFind(focusEditor: true);
    }

    private void CloseFind(bool focusEditor)
    {
        _findRefreshVersion++;
        FindPanel.Visibility = Visibility.Collapsed;

        var editorToFocus = _activeFindEditor ?? CurrentEditorTextBox;
        _activeFindPresenter?.EndFind();
        _primaryFindPresenter.Clear();
        _secondaryFindPresenter.Clear();
        _findSession.Reset();
        _activeFindPresenter = null;
        _activeFindEditor = null;
        _activeFindDocument = null;
        FindResultText.Text = string.Empty;

        if (focusEditor && CurrentDocument is not null)
            editorToFocus.Focus();
    }

    private void PositionFindPanel()
    {
        Grid.SetColumn(
            FindPanel,
            ReferenceEquals(CurrentDocument, _secondaryDocument) ? 2 : 0);
    }

    private void ShowReplaceToggle_Click(object sender, RoutedEventArgs e)
    {
        UpdateReplaceRowVisibility();
    }

    private void UpdateReplaceRowVisibility()
    {
        ReplaceRow.Visibility = ShowReplaceToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShowReplaceToggle.ToolTip = ShowReplaceToggle.IsChecked == true
            ? "Hide replace"
            : "Show replace";
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindPanel.Visibility != Visibility.Visible || _isSeedingFindQuery)
            return;

        var refreshVersion = ++_findRefreshVersion;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (refreshVersion == _findRefreshVersion
                    && FindPanel.Visibility == Visibility.Visible)
                {
                    RefreshFindResults(revealCurrent: true);
                }
            },
            DispatcherPriority.Background);
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            FindPrevious();
        else
            FindNext();
        e.Handled = true;
    }

    private void FindOption_Changed(object sender, RoutedEventArgs e)
    {
        if (FindPanel.Visibility == Visibility.Visible)
            RefreshFindResults(revealCurrent: true);
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
    }

    private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        FindPrevious();
    }

    private void FindNext() => MoveFind(forward: true);

    private void FindPrevious() => MoveFind(forward: false);

    private void MoveFind(bool forward)
    {
        RefreshFindResults(revealCurrent: false);
        if (_findSession.Matches.Count == 0)
            return;

        var presenter = ActivateFindPresenter();
        var manualAnchor = presenter.ConsumePendingManualNavigationAnchor();
        if (forward)
            _findSession.Next(manualAnchor);
        else
            _findSession.Previous(manualAnchor);
        presenter.Present(_findSession, bringCurrentIntoView: true);
        UpdateFindResultText();
    }

    private void RefreshFindResults(
        bool revealCurrent,
        int? navigationAnchor = null)
    {
        if (CurrentDocument is null)
        {
            _findSession.Reset();
            _activeFindPresenter?.Clear();
            FindResultText.Text = string.Empty;
            return;
        }

        var presenter = ActivateFindPresenter();
        _findSession.Refresh(
            CurrentEditorTextBox.Text,
            CurrentDocument,
            FindTextBox.Text,
            GetFindOptions(),
            navigationAnchor is { } anchor
                ? Math.Clamp(anchor, 0, CurrentEditorTextBox.Text.Length)
                : presenter.GetQueryRefreshAnchor());
        presenter.Present(_findSession, bringCurrentIntoView: revealCurrent);
        UpdateFindResultText();
    }

    private TextSearchOptions GetFindOptions() => new()
    {
        MatchCase = MatchCaseOption.IsChecked == true,
        MatchWholeWord = MatchWholeWordOption.IsChecked == true,
        UseRegex = RegexOption.IsChecked == true
    };

    private TextBoxSearchPresenter ActivateFindPresenter()
    {
        var editor = CurrentEditorTextBox;
        var presenter = ReferenceEquals(editor, SecondaryEditorTextBox)
            ? _secondaryFindPresenter
            : _primaryFindPresenter;

        if (ReferenceEquals(_activeFindEditor, editor) &&
            ReferenceEquals(_activeFindDocument, CurrentDocument))
        {
            return presenter;
        }

        _activeFindPresenter?.Clear();
        _findSession.Reset();
        _activeFindEditor = editor;
        _activeFindDocument = CurrentDocument;
        _activeFindPresenter = presenter;
        presenter.BeginFind();
        return presenter;
    }

    private void UpdateFindResultText()
    {
        FindResultText.Text = string.IsNullOrEmpty(FindTextBox.Text)
            ? string.Empty
            : _findSession.ValidationMessage ?? (_findSession.Matches.Count == 0
                ? "Not found"
                : $"{_findSession.CurrentMatchIndex + 1} of {_findSession.Matches.Count}");
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentEditorTextBox.IsReadOnly)
            return;

        RefreshFindResults(revealCurrent: false);
        if (_findSession.CurrentMatch is not { } match)
        {
            FindNext();
            return;
        }

        var nextAnchor = match.Start + ReplaceTextBox.Text.Length;
        ReplaceText(match.Start, match.Length, ReplaceTextBox.Text);
        ActivateFindPresenter().BeginFind();
        RefreshFindResults(revealCurrent: true, navigationAnchor: nextAnchor);
    }

    private void ReplaceAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentEditorTextBox.IsReadOnly)
            return;

        RefreshFindResults(revealCurrent: false);
        if (_findSession.Matches.Count == 0)
            return;

        var matches = _findSession.Matches.ToArray();
        var replacedCount = matches.Length;
        _isApplyingFindReplacement = true;
        CurrentEditorTextBox.BeginChange();
        try
        {
            for (var index = matches.Length - 1; index >= 0; index--)
            {
                var match = matches[index];
                CurrentEditorTextBox.Select(match.Start, match.Length);
                CurrentEditorTextBox.SelectedText = ReplaceTextBox.Text;
            }
        }
        finally
        {
            CurrentEditorTextBox.EndChange();
            _isApplyingFindReplacement = false;
        }

        StoreCurrentEditorChanges();
        ActivateFindPresenter().BeginFind();
        _findSession.Reset();
        RefreshFindResults(revealCurrent: false);
        FindResultText.Text = $"Replaced {replacedCount}";
    }

    private void ReplaceText(int start, int length, string replacement)
    {
        _isApplyingFindReplacement = true;
        CurrentEditorTextBox.BeginChange();
        try
        {
            CurrentEditorTextBox.Select(start, length);
            CurrentEditorTextBox.SelectedText = replacement;
        }
        finally
        {
            CurrentEditorTextBox.EndChange();
            _isApplyingFindReplacement = false;
        }

        StoreCurrentEditorChanges();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        TrySaveCurrentNote();
    }

    private void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        TrySaveCurrentNoteAs();
    }

    private bool TrySaveCurrentNoteAs()
    {
        if (CurrentDocument is not { } document)
            return true;

        CaptureActiveDocument();
        var dialog = new SaveFileDialog
        {
            Title = "Save Note As",
            InitialDirectory = Path.GetDirectoryName(document.FilePath),
            FileName = Path.GetFileName(document.FilePath),
            DefaultExt = Path.GetExtension(document.FilePath),
            AddExtension = true,
            OverwritePrompt = true,
            Filter = NoteFileExtensions.FileDialogFilter
        };
        if (dialog.ShowDialog(this) != true)
            return false;

        string newPath;
        try
        {
            newPath = NormalizePath(dialog.FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException || IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to save note", "The note could not be saved. Check file access and available disk space.");
            return false;
        }

        if (PathsEqual(document.FilePath, newPath))
            return TrySaveDocument(document);
        if (FindDocument(newPath) is not null)
        {
            ShowError(
                "Note is already open",
                "Close the existing tab for that file before saving another note over it.");
            return false;
        }

        try
        {
            var oldPath = document.FilePath;
            _noteSaveWarning = document.InitialFileContent is { } initialContent
                ? _contentService.SaveNewNoteAs(oldPath, newPath, document.Content, initialContent, document.Encoding)
                : _contentService.SaveAs(newPath, document.Content, document.Encoding);
            _workspace.UpdatePath(
                document,
                newPath,
                clearGeneratedName: true,
                markPresent: true,
                notifyChange: false);
            _documentTimeText.Remove(oldPath);
            _documentTimeText.Remove(newPath);
            _workspace.MarkSaved(document);
            if (ReferenceEquals(document, _secondaryDocument))
                SecondaryDocumentTitle.Text = document.TabLabel;
            _unsavedRecoveredPaths.Remove(oldPath);
            _unsavedRecoveredPaths.Remove(newPath);
            _discardedDocumentsPendingDraftDeletion.Remove(document);
            RefreshScheduledRecoveryDrafts();
            if (!TryDeleteRecoveryDraft(oldPath, out var cleanupError))
                ShowRecoveryCleanupWarning(cleanupError!);
            SaveEditorSession();
            UpdateEditorState();
            return true;
        }
        catch (NoteSaveConflictException ex)
        {
            _noteSaveWarning = ex.Message;
            ShowError("Note changed outside Noted", ex.Message);
            return false;
        }
        catch (FileVerificationException ex)
        {
            _noteSaveWarning = ex.Message;
            ShowError("Unable to verify saved note", ex.Message);
            return false;
        }
        catch (TextEncodingException ex)
        {
            ShowError("Unable to save note", ex.Message);
            return false;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to save note", "The note could not be saved. Check file access and available disk space.");
            return false;
        }
    }

    private void ShowInFileExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowActiveNoteInFileExplorer();
    }

    private void ShowActiveNoteInFileExplorer(Action<ProcessStartInfo>? startProcess = null)
    {
        if (CurrentDocument is not { } document)
            return;

        try
        {
            var path = document.FilePath;
            var arguments = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path)}\"";
            var startInfo = new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            };
            if (startProcess is not null) startProcess(startInfo);
            else Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception || FileSystemErrors.IsExpected(ex))
        {
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to open File Explorer", "File Explorer could not be opened for this note.");
        }
    }

    private void FileExplorerIntegrationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var enabled = FileExplorerIntegrationMenuItem.IsChecked;
        try
        {
            FileExplorerIntegrationService.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            FileExplorerIntegrationMenuItem.IsChecked = !enabled;
            ExceptionDiagnostics.Record(ex);
            ShowError("Unable to update File Explorer", "The File Explorer integration could not be updated. Check registry access.");
        }
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
        if (CurrentDocument is { } document)
            RequestNoteDeletion(document);
    }

    private void DeleteTabNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: OpenNoteDocument document })
            RequestNoteDeletion(document);
    }

    private void RenameTabNoteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: OpenNoteDocument document })
            return;
        if (document.IsMissing)
        {
            ShowError(
                "Unable to rename note",
                "The original file no longer exists, so this note cannot be renamed.");
            return;
        }

        if (ReferenceEquals(document, _activeDocument) || ReferenceEquals(document, _secondaryDocument))
            CaptureActiveDocument();
        TryRenameNote(document, isFirstSave: false);
    }

    private void RequestNoteDeletion(OpenNoteDocument document)
    {
        DeleteNoteRequested?.Invoke(this, new NoteDeleteRequestedEventArgs(document.FilePath));
    }

    private void OpenTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && !_isSelectingTab && OpenTabsList.SelectedItem is OpenNoteDocument document)
            OpenDocumentInFocusedPane(document, focusEditor: false, saveSession: true);
    }

    private void SelectTab(OpenNoteDocument? document)
    {
        if (ReferenceEquals(OpenTabsList.SelectedItem, document))
            return;
        _isSelectingTab = true;
        try { OpenTabsList.SelectedItem = document; }
        finally { _isSelectingTab = false; }
    }

    private void OpenDocumentInFocusedPane(
        OpenNoteDocument document,
        bool focusEditor,
        bool saveSession)
    {
        if (_workspace.ShouldOpenDocumentInSecondaryPane(document))
        {
            ShowSecondaryDocument(
                document,
                focusPane: true,
                focusEditor: focusEditor,
                saveSession: saveSession);
            return;
        }

        ActivateDocument(document, focusEditor);
        if (saveSession)
            SaveEditorSession();
    }

    private void OpenTabsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete ||
            OpenTabsList.SelectedItem is not OpenNoteDocument document)
        {
            return;
        }

        e.Handled = true;
        RequestNoteDeletion(document);
    }

    private void OpenTabsList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;

        _middlePressedDocument = FindDocumentFromElement(e.OriginalSource as DependencyObject);
        if (_middlePressedDocument is null)
            return;

        e.Handled = true;
    }

    private void OpenTabsList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;

        var pressedDocument = _middlePressedDocument;
        _middlePressedDocument = null;
        if (pressedDocument is null)
            return;

        e.Handled = true;
        var releasedDocument = FindDocumentFromElement(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(pressedDocument, releasedDocument))
            TryCloseDocument(pressedDocument);
    }

    private void OpenTabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _primaryBeforeTabDrag = _activeDocument;
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

        SideBySideDropTarget.Visibility = Visibility.Visible;
        try
        {
            DragDrop.DoDragDrop(OpenTabsList, _draggedDocument, DragDropEffects.Move);
        }
        finally
        {
            SideBySideDropTarget.Visibility = Visibility.Collapsed;
            _draggedDocument = null;
            _primaryBeforeTabDrag = null;
        }
    }

    private void SideBySideDropTarget_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(OpenNoteDocument))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SideBySideDropTarget_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(OpenNoteDocument)) &&
            e.Data.GetData(typeof(OpenNoteDocument)) is OpenNoteDocument document)
        {
            ShowSecondaryDocument(
                document,
                focusPane: true,
                focusEditor: true,
                saveSession: true);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void ShowSecondaryDocument(
        OpenNoteDocument document,
        bool focusPane,
        bool focusEditor,
        bool saveSession)
    {
        if (_documents.Count < 2)
            return;

        CaptureActiveDocument();
        if (ReferenceEquals(document, _activeDocument))
        {
            var primaryDocument = _secondaryDocument;
            if (primaryDocument is null && ReferenceEquals(document, _draggedDocument) &&
                !ReferenceEquals(_primaryBeforeTabDrag, document))
                primaryDocument = _primaryBeforeTabDrag;
            primaryDocument ??= _documents.FirstOrDefault(item => !ReferenceEquals(item, document));
            if (primaryDocument is null)
                return;
            if (ReferenceEquals(primaryDocument, _secondaryDocument))
                _workspace.CloseSecondary();
            ActivateDocument(primaryDocument, focusEditor: false);
        }

        _workspace.ShowSecondary(document, focusPane);
        _lastSecondaryDocumentId = document.DocumentId;
        _isLoadingSecondary = true;
        SecondaryEditorTextBox.Text = document.Content;
        SecondaryEditorTextBox.CaretIndex = Math.Clamp(document.CaretIndex, 0, document.Content.Length);
        SecondaryEditorTextBox.ScrollToVerticalOffset(Math.Max(0, document.VerticalOffset));
        ResetSecondaryUndoHistory();
        SecondaryDocumentTitle.Text = document.TabLabel;
        _isLoadingSecondary = false;
        _secondaryPreview.SetMode(EffectivePreviewMode(document));

        EditorSplitterColumn.Width = new GridLength(5);
        SecondaryEditorColumn.Width = new GridLength(1, GridUnitType.Star);
        EditorSplitter.Visibility = Visibility.Visible;
        SecondaryEditorPane.Visibility = Visibility.Visible;
        UpdateEditorState();
        if (focusEditor)
        {
            SelectTab(document);
            EditorWorkspace.UpdateLayout();
            FocusActiveSurface();
        }

        if (saveSession)
            SaveEditorSession();
    }

    private void CloseSecondaryPaneButton_Click(object sender, RoutedEventArgs e)
    {
        CloseSecondaryPane(saveSession: true);
        FocusActiveSurface();
    }

    private void ClosePrimaryPaneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_secondaryDocument is not { } remainingDocument)
            return;

        CaptureActiveDocument();
        _workspace.CloseSecondary();
        ClearSecondaryPaneSurface();
        ActivateDocument(remainingDocument);
        SaveEditorSession();
    }

    private void CloseSecondaryPane(bool saveSession)
    {
        if (_secondaryDocument is null)
            return;

        CaptureActiveDocument();
        _workspace.CloseSecondary();
        ClearSecondaryPaneSurface();
        UpdateEditorState();
        if (saveSession)
            SaveEditorSession();
    }

    private void ClearSecondaryPaneSurface()
    {
        _isLoadingSecondary = true;
        SecondaryEditorTextBox.Clear();
        ResetSecondaryUndoHistory();
        SecondaryDocumentTitle.Text = string.Empty;
        _isLoadingSecondary = false;
        EditorSplitterColumn.Width = new GridLength(0);
        SecondaryEditorColumn.Width = new GridLength(0);
        EditorSplitter.Visibility = Visibility.Collapsed;
        SecondaryEditorPane.Visibility = Visibility.Collapsed;
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
        _workspace.Move(source, targetIndex);
        SelectTab(CurrentDocument);
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

        TryCloseDocument(document);
        e.Handled = true;
    }

    private bool TryCloseDocument(OpenNoteDocument document)
    {
        CaptureActiveDocument();
        if (document.IsDirty && !TryResolveDirtyDocuments([document]))
            return false;

        return RemoveDocument(document, deleteRecoveryDraft: true, rememberClosed: true);
    }

    private void LineNumbersMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetLineNumbers(LineNumbersMenuItem.IsChecked);
        SaveWindowSize();
    }

    private void SetLineNumbers(bool visible)
    {
        LineNumbersMenuItem.IsChecked = visible;
        PrimaryLineNumbers.Visibility = SecondaryLineNumbers.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollSpeedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out var speed))
        {
            SetScrollSpeed(speed);
            SaveWindowSize();
        }
    }

    private void SetScrollSpeed(int speed)
    {
        _scrollSpeed = speed is >= 1 and <= 3 ? speed : 2;
        NormalScrollMenuItem.IsChecked = _scrollSpeed == 1;
        FastScrollMenuItem.IsChecked = _scrollSpeed == 2;
        FasterScrollMenuItem.IsChecked = _scrollSpeed == 3;
    }

    private void AlwaysVisibleMenuItem_Click(object sender, RoutedEventArgs e) =>
        WindowAppearance.SetAlwaysVisible(this, AlwaysVisibleMenuItem.IsChecked);

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Let the editor handle modified inputs and smaller touchpad movements
        if (sender is not TextBox editor || _scrollSpeed == 1 || Keyboard.Modifiers != ModifierKeys.None || e.Delta % 120 != 0) return;
        
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        
        var distance = lines < 0 ? editor.ViewportHeight : lines * editor.FontSize * 1.2;
        
        // Scroll one step in the wheel direction and apply the configured speed.
        editor.ScrollToVerticalOffset(editor.VerticalOffset - Math.Sign(e.Delta) * distance * _scrollSpeed);
        
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
        SecondaryEditorTextBox.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SecondaryEditorTextBox.HorizontalScrollBarVisibility = enabled
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        HeaderWordWrapMenuItem.IsChecked = enabled;
        ContextWordWrapMenuItem.IsChecked = enabled;
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
            ExceptionDiagnostics.Record(ex);
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
        if (TabsColumn.ActualWidth <= TabsPanelCollapseThreshold)
        {
            SetTabsPanelCollapsed(collapsed: true, captureCurrentWidth: false);
            return;
        }

        if (TabsColumn.ActualWidth < MinimumTabsPanelWidth)
            TabsColumn.Width = new GridLength(MinimumTabsPanelWidth);

        CaptureTabsPanelWidth();
        SaveWindowSize();
    }

    private void ToggleTabsPanel()
    {
        SetTabsPanelCollapsed(!_isTabsPanelCollapsed);
    }

    private void SetTabsPanelCollapsed(bool collapsed, bool captureCurrentWidth = true)
    {
        if (collapsed && captureCurrentWidth)
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
            TabsColumn.MinWidth = 0;
            TabsColumn.Width = new GridLength(_tabsPanelWidth);
            TabsSplitterColumn.Width = new GridLength(5);
        }
        TabsPanelMenuItem.IsChecked = !_isTabsPanelCollapsed;
        TabsPanelIconSidebar.Opacity = _isTabsPanelCollapsed ? 0.25 : 1;
        ToggleTabsButton.ToolTip = _isTabsPanelCollapsed
            ? "Show tabs (Ctrl+Shift+B)"
            : "Hide tabs (Ctrl+Shift+B)";
    }

    private void CaptureTabsPanelWidth()
    {
        if (_isTabsPanelCollapsed || TabsColumn.ActualWidth <= 0)
            return;

        _tabsPanelWidth = NormalizeTabsPanelWidth(TabsColumn.ActualWidth);
    }

    private MarkdownPreviewPresenter CurrentPreview => ReferenceEquals(CurrentDocument, _secondaryDocument)
        ? _secondaryPreview : _primaryPreview;

    private static MarkdownPreviewMode EffectivePreviewMode(OpenNoteDocument? document) =>
        document is not null && NoteFileExtensions.IsMarkdown(document.FilePath) && Enum.IsDefined(document.MarkdownPreviewMode)
            ? document.MarkdownPreviewMode : MarkdownPreviewMode.Off;

    private void AddMarkdownPreviewMenu(MenuItem menu, Func<OpenNoteDocument?> document)
    {
        foreach (var (label, mode) in new[]
        {
            ("Preview Off", MarkdownPreviewMode.Off),
            ("Preview", MarkdownPreviewMode.Preview),
            ("Open Preview to Side", MarkdownPreviewMode.SideBySide)
        })
        {
            var item = new MenuItem { Header = label, Tag = mode, IsCheckable = true };
            item.Click += (_, _) =>
            {
                if (document() is { } target)
                    SetMarkdownPreviewMode(target, mode);
            };
            menu.Items.Add(item);
        }
        _previewMenus.Add((menu, document));
    }

    private void UpdateMarkdownPreviewMenus()
    {
        // Reconcile file-type changes (including Save As) without changing source text.
        var primaryMode = EffectivePreviewMode(_activeDocument);
        var secondaryMode = EffectivePreviewMode(_secondaryDocument);
        if (_primaryPreview.Mode != primaryMode) _primaryPreview.SetMode(primaryMode);
        if (_secondaryPreview.Mode != secondaryMode) _secondaryPreview.SetMode(secondaryMode);
        foreach (var (menu, getDocument) in _previewMenus)
        {
            var document = getDocument();
            menu.Visibility = document is not null && NoteFileExtensions.IsMarkdown(document.FilePath)
                ? Visibility.Visible : Visibility.Collapsed;
            foreach (MenuItem item in menu.Items)
                item.IsChecked = (MarkdownPreviewMode)item.Tag == EffectivePreviewMode(document);
        }
    }

    private void ToggleMarkdownPreview()
    {
        if (CurrentDocument is { } document && NoteFileExtensions.IsMarkdown(document.FilePath))
            SetMarkdownPreviewMode(document, CurrentPreview.Mode == MarkdownPreviewMode.Off
                ? MarkdownPreviewMode.Preview : MarkdownPreviewMode.Off);
    }

    private void SetMarkdownPreviewEnabled(bool enabled, bool saveSession = true)
    {
        if (_activeDocument is { } document)
            SetMarkdownPreviewMode(document, enabled ? MarkdownPreviewMode.Preview : MarkdownPreviewMode.Off, saveSession);
        else
            _primaryPreview.SetMode(MarkdownPreviewMode.Off);
    }

    private void SetMarkdownPreviewMode(OpenNoteDocument document, MarkdownPreviewMode mode, bool saveSession = true)
    {
        if (mode == MarkdownPreviewMode.Preview && FindPanel.Visibility == Visibility.Visible)
            CloseFind(focusEditor: false);
        _workspace.SetMarkdownPreviewMode(document, mode);
        var presenter = ReferenceEquals(document, _secondaryDocument) ? _secondaryPreview : _primaryPreview;
        presenter.SetMode(EffectivePreviewMode(document));
        _workspace.Focus(document);
        UpdateEditorState();
        if (saveSession) SaveEditorSession();
        FocusActiveSurface();
    }

    private void FocusActiveSurface()
    {
        Control surface = CurrentPreview.Mode == MarkdownPreviewMode.Preview
            ? (ReferenceEquals(CurrentDocument, _secondaryDocument) ? SecondaryMarkdownPreview : MarkdownPreview)
            : CurrentEditorTextBox;
        surface.Focus();
        Keyboard.Focus(surface);
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
        var modifiers = e.KeyboardDevice.Modifiers;
        if (HandleNavigationShortcut(e.Key, modifiers))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && modifiers == ModifierKeys.Control)
        {
            OpenFind(showReplace: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.H && modifiers == ModifierKeys.Control)
        {
            OpenFind(showReplace: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3 && FindPanel.Visibility == Visibility.Visible)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift))
                FindPrevious();
            else
                FindNext();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && FindPanel.Visibility == Visibility.Visible)
        {
            CloseFind(focusEditor: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
            CurrentDocument is { } markdownDocument && NoteFileExtensions.IsMarkdown(markdownDocument.FilePath))
        {
            ToggleMarkdownPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            TrySaveCurrentNoteAs();
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

        if (HandleTabsPanelShortcut(e.Key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.W && modifiers == ModifierKeys.Control && CurrentDocument is { } currentDocument)
        {
            TryCloseDocument(currentDocument);
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

    internal bool HandleNavigationShortcut(Key key, ModifierKeys modifiers,
        Func<OpenFileDialog, bool?>? chooseFile = null, Func<QuickOpenDialog, bool?>? chooseNote = null,
        Action<ProcessStartInfo>? reveal = null)
    {
        if (key == Key.O && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ShowActiveNoteInFileExplorer(reveal);
            return true;
        }
        if (modifiers != ModifierKeys.Control) return false;
        switch (key)
        {
            case Key.O: OpenNoteFromDialog(chooseFile); return true;
            case Key.P: OpenQuickNote(chooseNote); return true;
            case Key.Oem5: ToggleSplitEditor(); return true;
            case Key.D1: FocusEditorPane(secondary: false); return true;
            case Key.D2: FocusEditorPane(secondary: true); return true;
            default: return false;
        }
    }

    private void ToggleSplitMenuItem_Click(object sender, RoutedEventArgs e) => ToggleSplitEditor();

    private void FocusPrimaryMenuItem_Click(object sender, RoutedEventArgs e) => FocusEditorPane(secondary: false);

    private void FocusSecondaryMenuItem_Click(object sender, RoutedEventArgs e) => FocusEditorPane(secondary: true);

    private void FocusEditorPane(bool secondary)
    {
        var document = secondary ? _secondaryDocument : _activeDocument;
        if (document is null) return;
        _workspace.Focus(document);
        UpdateEditorState();
        FocusActiveSurface();
    }

    private void ToggleSplitEditor()
    {
        if (_secondaryDocument is not null)
        {
            CloseSecondaryPane(saveSession: true);
            FocusActiveSurface();
            return;
        }
        var document = _lastSecondaryDocumentId.HasValue ? _workspace.FindById(_lastSecondaryDocumentId.Value) : null;
        if (document is null || ReferenceEquals(document, _activeDocument))
            document = _documents.FirstOrDefault(item => !ReferenceEquals(item, _activeDocument));
        if (document is not null) ShowSecondaryDocument(document, focusPane: true, focusEditor: true, saveSession: true);
    }

    internal bool HandleTabsPanelShortcut(Key key, ModifierKeys modifiers)
    {
        if (key != Key.B || modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
            return false;

        ToggleTabsPanel();
        return true;
    }

    private void CycleActiveDocument(bool backward)
    {
        if (CurrentDocument is null || _documents.Count < 2)
            return;

        var currentIndex = _documents.IndexOf(CurrentDocument);
        var nextIndex = backward
            ? (currentIndex - 1 + _documents.Count) % _documents.Count
            : (currentIndex + 1) % _documents.Count;
        OpenDocumentInFocusedPane(_documents[nextIndex], focusEditor: true, saveSession: true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isPreparedForApplicationClose)
            return;

        e.Cancel = true;
        _isHiddenTogether = false;
        CaptureActiveDocument();
        TryFlushRecoveryDraft(out _);
        SaveEditorSession();
        _reopenOnStartup = false;
        SaveWindowSize();

        Hide();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _recoverySaveScheduler.StateChanged -= RecoverySaveScheduler_StateChanged;
        _workspace.DocumentPathsChanged -= Workspace_DocumentPathsChanged;
        _recoverySaveScheduler.Dispose();
        _primaryPreview.Dispose();
        _secondaryPreview.Dispose();
        _primaryFindPresenter.Dispose();
        _secondaryFindPresenter.Dispose();
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

    private static bool IsExpectedFileException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);

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
