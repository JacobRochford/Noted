using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteOperationsTests
{
    [TestMethod]
    public void RenameNoteUpdatesTheOpenDocumentWithoutChangingItsIdentity()
    {
        using var directory = new TemporaryTestDirectory();
        var files = new StubNoteFileService(directory.Path);
        var workspace = new NoteEditorWorkspace();
        var oldPath = directory.File("old.txt");
        var document = CreateDocument(oldPath);
        var documentId = document.DocumentId;
        workspace.Add(document);
        NoteEditorDocumentPathChange? observedChange = null;
        workspace.DocumentPathsChanged += (_, e) => observedChange = e.Changes.Single();
        var operations = new NoteOperations(files, workspace);

        var result = operations.RenameNote(oldPath, "renamed");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(directory.File("renamed.txt"), result.NewFilePath);
        Assert.AreEqual(result.NewFilePath, document.FilePath);
        Assert.AreEqual(documentId, document.DocumentId);
        Assert.IsNotNull(observedChange);
        Assert.AreEqual(oldPath, observedChange.OldFilePath);
        Assert.AreEqual(result.NewFilePath, observedChange.NewFilePath);
    }

    [TestMethod]
    public void ExternalNoteRenameIsRejectedBeforeTouchingTheFileService()
    {
        using var directory = new TemporaryTestDirectory();
        using var externalDirectory = new TemporaryTestDirectory();
        var files = new StubNoteFileService(directory.Path);
        var operations = new NoteOperations(files, new NoteEditorWorkspace());

        var result = operations.RenameNote(externalDirectory.File("outside.txt"), "renamed");

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.IsExternalPath);
        Assert.AreEqual(0, files.RenameNoteCalls);
    }

    [TestMethod]
    public void RenameFolderUpdatesContainedDocumentsWithoutMatchingSiblingPrefixes()
    {
        using var directory = new TemporaryTestDirectory();
        var files = new StubNoteFileService(directory.Path);
        var workspace = new NoteEditorWorkspace();
        var contained = CreateDocument(Path.Combine(directory.Path, "Folder", "note.txt"));
        var sibling = CreateDocument(Path.Combine(directory.Path, "Folder Extra", "other.txt"));
        workspace.Add(contained);
        workspace.Add(sibling);
        var operations = new NoteOperations(files, workspace);

        var result = operations.RenameFolder(
            Path.Combine(directory.Path, "Folder"),
            "Renamed");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            Path.Combine(directory.Path, "Renamed", "note.txt"),
            contained.FilePath);
        Assert.AreEqual(
            Path.Combine(directory.Path, "Folder Extra", "other.txt"),
            sibling.FilePath);
    }

    [TestMethod]
    public void CreateNoteReturnsThePathAndKeyUsedByTheCaller()
    {
        using var directory = new TemporaryTestDirectory();
        var operations = new NoteOperations(
            new StubNoteFileService(directory.Path),
            new NoteEditorWorkspace());

        var result = operations.CreateNote("ideas");

        Assert.IsNotNull(result.Note);
        Assert.AreEqual(directory.File("ideas.txt"), result.FilePath);
        Assert.AreEqual("ideas.txt", result.NoteKey);
    }

    private static OpenNoteDocument CreateDocument(string path) =>
        new(path, string.Empty, string.Empty, isDirty: false);

    private sealed class StubNoteFileService(string notesDirectory) : INoteFileService
    {
        public string NotesDirectory { get; } = Path.GetFullPath(notesDirectory);
        public string DeletedNotesDirectory => Path.Combine(NotesDirectory, "DeletedNotes");
        public string ArchivedNotesDirectory => Path.Combine(NotesDirectory, ".archive");
        public string CurrentDirectory => NotesDirectory;
        public string CurrentFolderName => string.Empty;
        public bool CanNavigateUp => false;
        public bool CanNavigateBack => false;
        public bool CanNavigateForward => false;
        internal int RenameNoteCalls { get; private set; }

        public event EventHandler? FilesChanged;

        public string SuggestNoteName() => "suggested";

        public (CreatedNote? Note, string? Error) CreateNote(string? requestedName = null)
        {
            var name = requestedName ?? "quick";
            return (new CreatedNote($"{name}.txt", requestedName is null, string.Empty), null);
        }

        public bool TryGetNoteKey(string filePath, out string noteKey)
        {
            var relativePath = Path.GetRelativePath(NotesDirectory, Path.GetFullPath(filePath));
            var isContained = !Path.IsPathRooted(relativePath) &&
                              !relativePath.Equals("..", StringComparison.Ordinal) &&
                              !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                              !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
            noteKey = isContained ? relativePath.Replace('\\', '/') : string.Empty;
            return isContained;
        }

        public string GetNoteKey(string filePath)
        {
            if (!TryGetNoteKey(filePath, out var noteKey))
                throw new ArgumentException("Outside notes root.", nameof(filePath));
            return noteKey;
        }

        public (bool Success, string? NewFileName, string? Error) RenameNote(
            string oldFileName,
            string newDisplayName,
            string? containingDirectory = null)
        {
            RenameNoteCalls++;
            return (true, $"{newDisplayName}.txt", null);
        }

        public (bool Success, string? NewFolderName, string? Error) RenameFolder(
            string oldName,
            string newName) =>
            (true, newName, null);

        public bool DeleteNote(string fileName, string? containingDirectory = null) => true;
        public (bool Success, string? Error) DeleteFolder(string folderName) => (true, null);
        public IReadOnlyList<NoteItem> GetNotes() => [];
        public IReadOnlyList<ArchivedNoteItem> GetArchivedNotes() => [];
        public IReadOnlyList<string> GetAllNoteKeys() => [];
        public bool ChangeNotesDirectory(string newDirectory) => throw new NotSupportedException();
        public (bool Success, string? Error) ArchiveNote(string fileName, string? containingDirectory = null) => throw new NotSupportedException();
        public (bool Success, string? Error) RestoreArchivedNote(string relativePath) => throw new NotSupportedException();
        public void StartWatching() => FilesChanged?.Invoke(this, EventArgs.Empty);
        public IReadOnlyList<NoteItem> GetFolders() => [];
        public void NavigateTo(string folderName) => throw new NotSupportedException();
        public void NavigateUp() => throw new NotSupportedException();
        public void NavigateBack() => throw new NotSupportedException();
        public void NavigateForward() => throw new NotSupportedException();
        public IReadOnlyList<NoteItem> GetNotesInSubfolder(string subfolderName) => [];
        public (bool Success, string? Error) CreateFolder(string folderName) => throw new NotSupportedException();
        public void Dispose()
        {
        }
    }
}
