using Noted.Models;

namespace Noted.Services;

public interface INoteFileService : IDisposable {
    string NotesDirectory { get; }
    string DeletedNotesDirectory { get; }
    event EventHandler? FilesChanged;
    IReadOnlyList<NoteItem> GetNotes();
    IReadOnlyList<string> GetAllNoteKeys();
    string GetNoteKey(string filePath);
    string CreateNote(string? requestedName = null);
    bool ChangeNotesDirectory(string newDirectory);
    bool DeleteNote(string fileName, string? containingDirectory = null);
    (bool Success, string? NewFileName, string? Error) RenameNote(
        string oldFileName,
        string newDisplayName,
        string? containingDirectory = null);
    void StartWatching();
    string CurrentDirectory { get; }
    string CurrentFolderName { get; }   // empty if at root
    bool CanNavigateUp { get; }
    bool CanNavigateBack { get; }
    bool CanNavigateForward { get; }
    IReadOnlyList<NoteItem> GetFolders();
    void NavigateTo(string folderName);
    void NavigateUp();
    void NavigateBack();
    void NavigateForward();
    
    // folder CRUD
    IReadOnlyList<NoteItem> GetNotesInSubfolder(string subfolderName);
    (bool Success, string? Error) CreateFolder(string folderName);
    (bool Success, string? NewFolderName, string? Error) RenameFolder(string oldName, string newName);
    (bool Success, string? Error) DeleteFolder(string folderName);
}
