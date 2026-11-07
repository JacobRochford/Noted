using System.IO;
using Noted.Models;

namespace Noted.Services;

internal sealed record NoteEditorWorkspaceLoadResult(
    IReadOnlyList<OpenNoteDocument> Documents,
    Guid? ActiveDocumentId,
    Guid? SecondaryDocumentId,
    IReadOnlyList<NoteRecoveryIssue> RecoveryIssues,
    IReadOnlyList<NoteEditorSessionIssue> SessionIssues,
    string? RecoveryOperationError);

internal sealed class NoteEditorPersistence
{
    private readonly INoteContentService _contentService;
    private readonly INoteRecoveryService _recoveryService;
    private readonly INoteEditorSessionService _sessionService;
    private string? _recoveryOperationError;
    private readonly Func<IndentationSettings> _getDefaultIndentation;
    private readonly NoteDraftStorage? _draftStorage;
    private readonly Dictionary<string, Guid> _completedDraftTransitions = new(StringComparer.OrdinalIgnoreCase);

    internal NoteEditorPersistence(
        INoteContentService contentService,
        INoteRecoveryService recoveryService,
        INoteEditorSessionService sessionService,
        Func<IndentationSettings>? getDefaultIndentation = null,
        NoteDraftStorage? draftStorage = null)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(sessionService);
        _contentService = contentService;
        _recoveryService = recoveryService;
        _sessionService = sessionService;
        _getDefaultIndentation = getDefaultIndentation ?? (() => new());
        _draftStorage = draftStorage;
    }

    internal NoteEditorWorkspaceLoadResult LoadWorkspace(bool loadSavedSession)
    {
        _recoveryOperationError = null;
        var savedSession = loadSavedSession || _draftStorage is not null
            ? _sessionService.Load()
            : new NoteEditorSessionLoadResult(new NoteEditorSession(), []);
        _completedDraftTransitions.Clear();
        foreach (var pair in savedSession.Session.CompletedDraftTransitions)
            _completedDraftTransitions[pair.Key] = pair.Value;
        var sessionLoadResult = loadSavedSession ? savedSession
            : savedSession with { Session = new NoteEditorSession() };
        var recoveryLoadResult = _recoveryService.LoadDrafts();
        var recoveryIssues = recoveryLoadResult.Issues.ToList();
        var drafts = NormalizeDrafts(recoveryLoadResult.Drafts);
        var documents = new List<OpenNoteDocument>();
        var documentsByPath = new Dictionary<string, OpenNoteDocument>(StringComparer.OrdinalIgnoreCase);
        var retiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathsNeedingReview = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedPaths = new List<string>();
        if (_draftStorage is not null)
        {
            try
            {
                foreach (var path in _draftStorage.GetDraftPaths())
                {
                    try
                    {
                        var state = _draftStorage.Read(path);
                        if (state?.CleanupCommitted == true || state is not null && state.CleanupToken != Guid.Empty &&
                            _completedDraftTransitions.TryGetValue(path, out var token) && token == state.CleanupToken)
                        {
                            if (state?.CleanupContentHash is null)
                                throw new IOException("The completed Quick Note transition needs review; its remaining files were kept.");
                            if (!state.CleanupCommitted)
                                _draftStorage.CommitCleanup(path);
                            retiredPaths.Add(path);
                            _draftStorage.Cleanup(path, _recoveryService);
                            _completedDraftTransitions.Remove(path);
                        }
                        else
                            managedPaths.Add(path);
                    }
                    catch (NoteDraftCleanupBlockedException ex)
                    {
                        retiredPaths.Remove(path);
                        _completedDraftTransitions.Remove(path);
                        managedPaths.Add(path);
                        pathsNeedingReview.Add(path);
                        recoveryIssues.Add(new NoteRecoveryIssue(path, ex.Message));
                        try { _draftStorage.CancelCleanup(path); }
                        catch (Exception cleanupError) when (IsExpectedFileException(cleanupError))
                        {
                            recoveryIssues.Add(new NoteRecoveryIssue(path, cleanupError.Message));
                        }
                    }
                    catch (Exception ex) when (IsExpectedFileException(ex))
                    {
                        recoveryIssues.Add(new NoteRecoveryIssue(path, ex.Message));
                        if (!retiredPaths.Contains(path))
                        {
                            managedPaths.Add(path);
                            pathsNeedingReview.Add(path);
                        }
                    }
                }
            }
            catch (Exception ex) when (IsExpectedFileException(ex))
            {
                recoveryIssues.Add(new NoteRecoveryIssue(_draftStorage.DirectoryPath, ex.Message));
            }
        }

        foreach (var tabState in sessionLoadResult.Session.Tabs)
        {
            if (!TryNormalizeSupportedPath(tabState.FilePath, out var normalizedPath) ||
                retiredPaths.Contains(normalizedPath) || documentsByPath.ContainsKey(normalizedPath) ||
                !TryRestoreSessionDocument(
                    tabState,
                    normalizedPath,
                    drafts,
                    recoveryIssues,
                    out var document))
            {
                continue;
            }

            documents.Add(document);
            documentsByPath.Add(normalizedPath, document);
        }

        foreach (var draft in drafts.Values)
        {
            if (retiredPaths.Contains(draft.FilePath) || documentsByPath.ContainsKey(draft.FilePath) ||
                !TryRestoreDraftOnlyDocument(draft, recoveryIssues, out var document))
            {
                continue;
            }

            documents.Add(document);
            documentsByPath.Add(document.FilePath, document);
        }

        foreach (var path in managedPaths.Where(path => !documentsByPath.ContainsKey(path)))
        {
            if (TryRestoreSessionDocument(new NoteEditorTabState { FilePath = path }, path,
                drafts, recoveryIssues, out var document))
            {
                documents.Add(document);
                documentsByPath.Add(path, document);
            }
        }

        foreach (var document in documents.Where(document => pathsNeedingReview.Contains(document.FilePath)))
        {
            document.IsDirty = true;
            document.InitialFileContent = null;
        }

        var activeDocument = FindBySavedPath(
                sessionLoadResult.Session.ActiveFilePath,
                documentsByPath)
            ?? documents.FirstOrDefault();
        var secondaryDocument = FindBySavedPath(
            sessionLoadResult.Session.SecondaryFilePath,
            documentsByPath);
        if (secondaryDocument?.DocumentId == activeDocument?.DocumentId)
            secondaryDocument = null;

        return new NoteEditorWorkspaceLoadResult(
            documents,
            activeDocument?.DocumentId,
            secondaryDocument?.DocumentId,
            recoveryIssues,
            sessionLoadResult.Issues,
            _recoveryOperationError);
    }

    internal PersistenceSaveResult SaveSession(NoteEditorWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        try
        {
            var warning = _sessionService.Save(CreateSession(workspace) with
            {
                CompletedDraftTransitions = new Dictionary<string, Guid>(_completedDraftTransitions, StringComparer.OrdinalIgnoreCase)
            });
            return PersistenceSaveResult.Succeeded(warning);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return PersistenceSaveResult.Failed("The note editor session could not be saved.");
        }
    }

    internal static NoteEditorSession CreateSession(NoteEditorWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new NoteEditorSession
        {
            Tabs = workspace.Documents.Select(NoteEditorWorkspace.CaptureTabState).ToList(),
            ActiveFilePath = workspace.ActiveDocument?.FilePath,
            SecondaryFilePath = workspace.SecondaryDocument?.FilePath
        };
    }

    internal string? SaveManagedDraft(NoteEditorWorkspace workspace, OpenNoteDocument document,
        string destination, NoteSaveDestination destinationState)
    {
        var storage = _draftStorage ?? throw new InvalidOperationException("Managed draft storage is unavailable.");
        if (storage.ContainsPath(destination))
            throw new IOException("Choose a permanent location outside Noted's managed Drafts folder.");
        var source = document.FilePath;
        _recoveryService.SaveDraft(source, document.Content);
        var token = storage.PrepareCleanup(source, document.Content, document.SavedContent, destination, document.Encoding);
        var warning = _contentService.SaveAs(destination, document.Content, document.Encoding, destinationState);
        var session = CreateSession(workspace);
        var completed = new Dictionary<string, Guid>(_completedDraftTransitions, StringComparer.OrdinalIgnoreCase) { [source] = token };
        // the session is the commit point; source cleanup cannot precede this write.
        _sessionService.Save(session with
        {
            Tabs = session.Tabs.Select(tab => PathsEqual(tab.FilePath, source) ? tab with
            {
                FilePath = destination, InitialFileContent = null, UsesGeneratedName = false, PendingEncoding = null
            } : tab).ToArray(),
            ActiveFilePath = PathsEqual(session.ActiveFilePath, source) ? destination : session.ActiveFilePath,
            SecondaryFilePath = PathsEqual(session.SecondaryFilePath, source) ? destination : session.SecondaryFilePath,
            CompletedDraftTransitions = completed
        });
        _completedDraftTransitions[source] = token;
        workspace.UpdatePath(document, destination, clearGeneratedName: true, markPresent: true, notifyChange: false);
        workspace.MarkSaved(document);
        var cleanupWarning = FinishCleanup(source);
        var message = string.Join(" ", new[] { warning, cleanupWarning }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrEmpty(message) ? null : message;
    }

    internal string? DiscardManagedDraft(NoteEditorWorkspace workspace, OpenNoteDocument document)
    {
        var storage = _draftStorage ?? throw new InvalidOperationException("Managed draft storage is unavailable.");
        var source = document.FilePath;
        _recoveryService.SaveDraft(source, document.Content);
        var token = storage.PrepareCleanup(source, document.Content, document.SavedContent);
        var session = CreateSession(workspace);
        var tabs = session.Tabs.Where(tab => !PathsEqual(tab.FilePath, source)).ToArray();
        var active = PathsEqual(session.ActiveFilePath, source) ? tabs.FirstOrDefault()?.FilePath : session.ActiveFilePath;
        var secondary = PathsEqual(session.SecondaryFilePath, source) || PathsEqual(session.SecondaryFilePath, active)
            ? null : session.SecondaryFilePath;
        var completed = new Dictionary<string, Guid>(_completedDraftTransitions, StringComparer.OrdinalIgnoreCase) { [source] = token };
        _sessionService.Save(session with { Tabs = tabs, ActiveFilePath = active, SecondaryFilePath = secondary, CompletedDraftTransitions = completed });
        _completedDraftTransitions[source] = token;
        return FinishCleanup(source);
    }

    private string? FinishCleanup(string source)
    {
        try
        {
            _draftStorage!.CommitCleanup(source);
            _draftStorage.Cleanup(source, _recoveryService);
            _completedDraftTransitions.Remove(source);
            return null;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            return $"Quick Note cleanup is pending for '{source}'. Its remaining files were kept for retry. {ex.Message}";
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, NoteRecoveryDraft> NormalizeDrafts(
        IReadOnlyList<NoteRecoveryDraft> drafts)
    {
        var normalizedDrafts = new Dictionary<string, NoteRecoveryDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            if (!TryNormalizeSupportedPath(draft.FilePath, out var normalizedPath))
                continue;

            var normalizedDraft = draft with { FilePath = normalizedPath };
            if (!normalizedDrafts.TryGetValue(normalizedPath, out var existing) ||
                normalizedDraft.UpdatedUtc > existing.UpdatedUtc)
            {
                normalizedDrafts[normalizedPath] = normalizedDraft;
            }
        }

        return normalizedDrafts;
    }

    private bool TryRestoreSessionDocument(
        NoteEditorTabState tabState,
        string normalizedPath,
        IReadOnlyDictionary<string, NoteRecoveryDraft> drafts,
        ICollection<NoteRecoveryIssue> issues,
        out OpenNoteDocument document)
    {
        document = null!;
        try
        {
            var managed = _draftStorage?.IsDraftPath(normalizedPath) == true;
            if (!File.Exists(normalizedPath))
            {
                if (!drafts.TryGetValue(normalizedPath, out var missingDraft))
                    return false;

                document = CreateDocument(
                    tabState,
                    normalizedPath,
                    missingDraft.Content,
                    savedContent: string.Empty,
                    isDirty: true,
                    isMissing: !managed);
                return true;
            }

            var loadedFile = _contentService.LoadWithEncoding(normalizedPath);
            var persistedContent = loadedFile.Content;
            drafts.TryGetValue(normalizedPath, out var draft);
            var content = draft is not null &&
                          !string.Equals(draft.Content, persistedContent, StringComparison.Ordinal)
                ? draft.Content
                : persistedContent;
            if (draft is not null && string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
                TryDeleteRedundantDraft(normalizedPath, issues);

            document = CreateDocument(
                tabState,
                normalizedPath,
                content,
                persistedContent,
                isDirty: !string.Equals(content, persistedContent, StringComparison.Ordinal),
                isMissing: false,
                encoding: loadedFile.Encoding);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
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
                    isMissing: true,
                    indentation: _getDefaultIndentation());
                ApplyDraftMetadata(document);
                return true;
            }

            var loadedFile = _contentService.LoadWithEncoding(draft.FilePath);
            var persistedContent = loadedFile.Content;
            if (string.Equals(draft.Content, persistedContent, StringComparison.Ordinal) &&
                _draftStorage?.IsDraftPath(draft.FilePath) != true)
            {
                TryDeleteRedundantDraft(draft.FilePath, issues);
                return false;
            }

            document = new OpenNoteDocument(
                draft.FilePath,
                draft.Content,
                persistedContent,
                isDirty: true,
                caretIndex: draft.Content.Length,
                indentation: _getDefaultIndentation(),
                encoding: loadedFile.Encoding);
            ApplyDraftMetadata(document);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            issues.Add(new NoteRecoveryIssue(
                draft.FilePath,
                $"The original document '{draft.FilePath}' could not be read. " +
                "Its recovery draft remains preserved in Noted's recovery folder. " +
                "Resolve the file access or encoding problem and restart Noted to retry recovery."));
            return false;
        }
    }

    private void TryDeleteRedundantDraft(
        string filePath,
        ICollection<NoteRecoveryIssue> issues)
    {
        try
        {
            _recoveryService.DeleteDraft(filePath);
        }
        catch (Exception ex) when (IsExpectedFileException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            var error =
                $"Recovery data for '{Path.GetFileName(filePath)}' could not be removed.";
            _recoveryOperationError = error;
            issues.Add(new NoteRecoveryIssue(
                filePath,
                error));
        }
    }

    private OpenNoteDocument CreateDocument(
        NoteEditorTabState tabState,
        string filePath,
        string content,
        string savedContent,
        bool isDirty,
        bool isMissing,
        TextFileEncoding encoding = TextFileEncoding.Utf8)
    {
        var document = new OpenNoteDocument(
            filePath,
            content,
            savedContent,
            isDirty,
            caretIndex: Math.Clamp(tabState.CaretIndex, 0, content.Length),
            verticalOffset: tabState.VerticalOffset,
            markdownPreviewEnabled: false,
            isMissing: isMissing,
            usesGeneratedName: tabState.UsesGeneratedName,
            initialFileContent: tabState.InitialFileContent,
            indentation: tabState.Indentation ?? _getDefaultIndentation(),
            encoding: encoding);
        document.MarkdownPreviewMode = Enum.IsDefined(tabState.MarkdownPreviewMode)
            ? tabState.MarkdownPreviewMode
            : MarkdownPreviewMode.Off;
        if (tabState.PendingEncoding is { } pending && Enum.IsDefined(pending))
        {
            document.Encoding = pending;
            document.IsDirty |= pending != document.SavedEncoding;
        }
        ApplyDraftMetadata(document);
        return document;
    }

    private void ApplyDraftMetadata(OpenNoteDocument document)
    {
        if (_draftStorage?.IsDraftPath(document.FilePath) != true)
            return;
        NoteDraftState? state;
        try { state = _draftStorage.Read(document.FilePath); }
        catch (Exception ex) when (IsExpectedFileException(ex)) { state = null; }
        document.UsesGeneratedName = true;
        document.InitialFileContent = state?.InitialContent;
        document.IsMissing = false;
        document.IsDirty = state is null || document.Content != state.InitialContent || document.Encoding != document.SavedEncoding;
    }

    private static OpenNoteDocument? FindBySavedPath(
        string? savedPath,
        IReadOnlyDictionary<string, OpenNoteDocument> documentsByPath) =>
        TryNormalizeSupportedPath(savedPath, out var normalizedPath) &&
        documentsByPath.TryGetValue(normalizedPath, out var document)
            ? document
            : null;

    private static bool TryNormalizeSupportedPath(string? path, out string normalizedPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                normalizedPath = string.Empty;
                return false;
            }

            normalizedPath = Path.GetFullPath(path);
            return NoteFileExtensions.IsSupported(normalizedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            ExceptionDiagnostics.Record(ex);
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool IsExpectedFileException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);
}
