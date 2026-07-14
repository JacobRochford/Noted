using Noted.Models;

namespace Noted.Services;

public interface INoteFileService : IDisposable {
    string NotesDirectory { get; }
    string DeletedNotesDirectory { get; }
    event EventHandler? FilesChanged;
    IReadOnlyList<NoteItem> GetNotes();
    string CreateNote(string? requestedName = null);
    bool ChangeNotesDirectory(string newDirectory);
    void DeleteNote(string fileName, string? containingDirectory = null);
    (bool Success, string? NewFileName, string? Error) RenameNote(
        string oldFileName,
        string newDisplayName,
        string? containingDirectory = null);
    void StartWatching();
    string CurrentDirectory { get; }
    string CurrentFolderName { get; }   // empty if at root
    bool CanNavigateUp { get; }
    IReadOnlyList<NoteItem> GetFolders();
    void NavigateTo(string folderName);
    void NavigateUp();
    
    // folder CRUD
    IReadOnlyList<NoteItem> GetNotesInSubfolder(string subfolderName);
    (bool Success, string? Error) CreateFolder(string folderName);
    (bool Success, string? Error) RenameFolder(string oldName, string newName);
    (bool Success, string? Error) DeleteFolder(string folderName);
}
