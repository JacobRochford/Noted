using System.Collections.ObjectModel;
using System.IO;

namespace Noted.Models;

internal sealed record NoteEditorWorkspaceCloseResult(
    bool ActiveDocumentChanged,
    bool SecondaryDocumentClosed,
    OpenNoteDocument? ActiveDocument);

internal sealed class NoteEditorWorkspace
{
    private readonly ObservableCollection<OpenNoteDocument> _documents = [];

    internal NoteEditorWorkspace()
    {
        Documents = new ReadOnlyObservableCollection<OpenNoteDocument>(_documents);
    }

    internal ReadOnlyObservableCollection<OpenNoteDocument> Documents { get; }
    internal OpenNoteDocument? ActiveDocument { get; private set; }
    internal OpenNoteDocument? SecondaryDocument { get; private set; }
    internal OpenNoteDocument? FocusedDocument { get; private set; }
    internal OpenNoteDocument? CurrentDocument => FocusedDocument ?? ActiveDocument;
    internal bool IsDirty => _documents.Any(document => document.IsDirty);

    internal void Add(OpenNoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_documents.Any(item => item.DocumentId == document.DocumentId))
            throw new InvalidOperationException("The document is already part of this workspace.");
        if (_documents.Any(item => PathsEqual(item.FilePath, document.FilePath)))
            throw new InvalidOperationException("A document with the same path is already open.");

        _documents.Add(document);
    }

    internal OpenNoteDocument? FindByPath(string normalizedPath) =>
        _documents.FirstOrDefault(document => PathsEqual(document.FilePath, normalizedPath));

    internal OpenNoteDocument? FindById(Guid documentId) =>
        _documents.FirstOrDefault(document => document.DocumentId == documentId);

    internal void Activate(OpenNoteDocument document)
    {
        EnsureOwned(document);
        if (!ReferenceEquals(SecondaryDocument, document))
            ActiveDocument = document;
        FocusedDocument = document;
    }

    internal void Focus(OpenNoteDocument? document)
    {
        if (document is not null &&
            !ReferenceEquals(document, ActiveDocument) &&
            !ReferenceEquals(document, SecondaryDocument))
        {
            throw new InvalidOperationException("Only a visible document can receive focus.");
        }

        FocusedDocument = document ?? ActiveDocument;
    }

    internal void ShowSecondary(OpenNoteDocument document, bool focus)
    {
        EnsureOwned(document);
        if (_documents.Count < 2)
            throw new InvalidOperationException("At least two documents are required for side-by-side editing.");
        if (ReferenceEquals(document, ActiveDocument))
            throw new InvalidOperationException("The active document cannot also be the secondary document.");

        SecondaryDocument = document;
        if (focus)
            FocusedDocument = document;
        else if (!ReferenceEquals(FocusedDocument, ActiveDocument))
            FocusedDocument = ActiveDocument;
    }

    internal void CloseSecondary()
    {
        SecondaryDocument = null;
        FocusedDocument = ActiveDocument;
    }

    internal void UpdateContent(OpenNoteDocument document, string content, int caretIndex)
    {
        EnsureOwned(document);
        ArgumentNullException.ThrowIfNull(content);
        document.Content = content;
        document.CaretIndex = Math.Clamp(caretIndex, 0, content.Length);
        document.IsDirty = !string.Equals(content, document.SavedContent, StringComparison.Ordinal);
    }

    internal void UpdateViewState(
        OpenNoteDocument document,
        int caretIndex,
        double verticalOffset,
        bool? markdownPreviewEnabled = null)
    {
        EnsureOwned(document);
        document.CaretIndex = Math.Clamp(caretIndex, 0, document.Content.Length);
        document.VerticalOffset = Math.Max(0, verticalOffset);
        if (markdownPreviewEnabled.HasValue)
            document.MarkdownPreviewEnabled = markdownPreviewEnabled.Value;
    }

    internal void SetMarkdownPreview(OpenNoteDocument document, bool enabled)
    {
        EnsureOwned(document);
        document.MarkdownPreviewEnabled = enabled;
    }

    internal void UpdatePath(
        OpenNoteDocument document,
        string filePath,
        bool clearGeneratedName,
        bool markPresent)
    {
        EnsureOwned(document);
        var normalizedPath = Path.GetFullPath(filePath);
        if (_documents.Any(item =>
                !ReferenceEquals(item, document) &&
                PathsEqual(item.FilePath, normalizedPath)))
        {
            throw new InvalidOperationException("A document with the same path is already open.");
        }

        document.UpdateFilePath(normalizedPath);
        if (clearGeneratedName)
            document.UsesGeneratedName = false;
        if (markPresent)
            document.IsMissing = false;
    }

    internal void UpdateCreationMetadata(
        OpenNoteDocument document,
        bool usesGeneratedName,
        string? initialFileContent)
    {
        EnsureOwned(document);
        if (usesGeneratedName)
            document.UsesGeneratedName = true;
        if (initialFileContent is not null)
            document.InitialFileContent = initialFileContent;
    }

    internal void MarkSaved(OpenNoteDocument document)
    {
        EnsureOwned(document);
        document.InitialFileContent = null;
        document.SavedContent = document.Content;
        document.IsDirty = false;
        document.IsMissing = false;
    }

    internal void MarkMissing(OpenNoteDocument document)
    {
        EnsureOwned(document);
        document.IsMissing = true;
    }

    internal NoteEditorWorkspaceCloseResult Close(OpenNoteDocument document)
    {
        EnsureOwned(document);
        var index = _documents.IndexOf(document);
        var activeChanged = ReferenceEquals(document, ActiveDocument);
        var secondaryClosed = ReferenceEquals(document, SecondaryDocument);

        if (secondaryClosed)
            SecondaryDocument = null;

        _documents.RemoveAt(index);
        if (activeChanged)
        {
            ActiveDocument = _documents.Count == 0
                ? null
                : _documents[Math.Clamp(index, 0, _documents.Count - 1)];
            if (ReferenceEquals(ActiveDocument, SecondaryDocument))
            {
                SecondaryDocument = null;
                secondaryClosed = true;
            }
            FocusedDocument = ActiveDocument;
        }
        else if (ReferenceEquals(FocusedDocument, document))
        {
            FocusedDocument = ActiveDocument;
        }

        return new NoteEditorWorkspaceCloseResult(
            activeChanged,
            secondaryClosed,
            ActiveDocument);
    }

    internal void Move(OpenNoteDocument document, int targetIndex)
    {
        EnsureOwned(document);
        if (targetIndex < 0 || targetIndex >= _documents.Count)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        _documents.Move(_documents.IndexOf(document), targetIndex);
    }

    private void EnsureOwned(OpenNoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_documents.Contains(document))
            throw new InvalidOperationException("The document is not part of this workspace.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
