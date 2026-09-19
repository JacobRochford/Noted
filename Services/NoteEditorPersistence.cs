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

    internal NoteEditorPersistence(
        INoteContentService contentService,
        INoteRecoveryService recoveryService,
        INoteEditorSessionService sessionService)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(sessionService);
        _contentService = contentService;
        _recoveryService = recoveryService;
        _sessionService = sessionService;
    }

    internal NoteEditorWorkspaceLoadResult LoadWorkspace(bool loadSavedSession)
    {
        _recoveryOperationError = null;
        var sessionLoadResult = loadSavedSession
            ? _sessionService.Load()
            : new NoteEditorSessionLoadResult(new NoteEditorSession(), []);
        var recoveryLoadResult = _recoveryService.LoadDrafts();
        var recoveryIssues = recoveryLoadResult.Issues.ToList();
        var drafts = NormalizeDrafts(recoveryLoadResult.Drafts);
        var documents = new List<OpenNoteDocument>();
        var documentsByPath = new Dictionary<string, OpenNoteDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var tabState in sessionLoadResult.Session.Tabs)
        {
            if (!TryNormalizeSupportedPath(tabState.FilePath, out var normalizedPath) ||
                documentsByPath.ContainsKey(normalizedPath) ||
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
            if (documentsByPath.ContainsKey(draft.FilePath) ||
                !TryRestoreDraftOnlyDocument(draft, recoveryIssues, out var document))
            {
                continue;
            }

            documents.Add(document);
            documentsByPath.Add(document.FilePath, document);
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
            var warning = _sessionService.Save(CreateSession(workspace));
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
            Tabs = workspace.Documents.Select(document => new NoteEditorTabState
            {
                FilePath = document.FilePath,
                CaretIndex = document.CaretIndex,
                VerticalOffset = document.VerticalOffset,
                MarkdownPreviewEnabled = document.MarkdownPreviewEnabled,
                UsesGeneratedName = document.UsesGeneratedName,
                InitialFileContent = document.InitialFileContent
            }).ToList(),
            ActiveFilePath = workspace.ActiveDocument?.FilePath,
            SecondaryFilePath = workspace.SecondaryDocument?.FilePath
        };
    }

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
                TryDeleteRedundantDraft(normalizedPath, issues);

            document = CreateDocument(
                tabState,
                normalizedPath,
                content,
                persistedContent,
                isDirty: !string.Equals(content, persistedContent, StringComparison.Ordinal),
                isMissing: false);
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
                    isMissing: true);
                return true;
            }

            var persistedContent = _contentService.Load(draft.FilePath);
            if (string.Equals(draft.Content, persistedContent, StringComparison.Ordinal))
            {
                TryDeleteRedundantDraft(draft.FilePath, issues);
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
            ExceptionDiagnostics.Record(ex);
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

    private static OpenNoteDocument CreateDocument(
        NoteEditorTabState tabState,
        string filePath,
        string content,
        string savedContent,
        bool isDirty,
        bool isMissing) =>
        new(
            filePath,
            content,
            savedContent,
            isDirty,
            caretIndex: Math.Clamp(tabState.CaretIndex, 0, content.Length),
            verticalOffset: tabState.VerticalOffset,
            markdownPreviewEnabled: tabState.MarkdownPreviewEnabled,
            isMissing: isMissing,
            usesGeneratedName: tabState.UsesGeneratedName,
            initialFileContent: tabState.InitialFileContent);

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
