namespace Noted.Services;

public interface IAppSettingsService {
    string StorageDirectory { get; }
    string? LoadNotesDirectory();
    void SaveNotesDirectory(string notesDirectory);
    IReadOnlyList<string> LoadPinnedNotes();
    void SavePinnedNotes(IReadOnlyList<string> pinnedNotes);
    bool LoadShowModifiedSubtitle();
    void SaveShowModifiedSubtitle(bool showModifiedSubtitle);
    NoteTimestampPlacement LoadTimestampPlacement();
    void SaveTimestampPlacement(NoteTimestampPlacement timestampPlacement);
    bool LoadPromptForNoteName();
    void SavePromptForNoteName(bool promptForNoteName);
    string? LoadCustomHeader();
    void SaveCustomHeader(string? customHeader);
    (string modifiers, string key) LoadGlobalHotkey();
    void SaveGlobalHotkey(string modifiers, string key);
    bool LoadGhostModeEnabled();
    void SaveGhostModeEnabled(bool enabled);
    double LoadGhostModeOpacity();
    void SaveGhostModeOpacity(double opacity);
    double LoadDefaultOpacity();
    void SaveDefaultOpacity(double opacity);
    FolderNavigationMode LoadFolderNavigationMode();
    void SaveFolderNavigationMode(FolderNavigationMode mode);
    IReadOnlyList<ChecklistItemData> LoadChecklistItems();
    void SaveChecklistItems(IReadOnlyList<ChecklistItemData> items);
    ChecklistWindowState LoadChecklistWindowState();
    void SaveChecklistWindowState(ChecklistWindowState state);
    IReadOnlyList<DictionaryItemData> LoadDictionaryItems();
    void SaveDictionaryItems(IReadOnlyList<DictionaryItemData> items);
    DictionaryWindowState LoadDictionaryWindowState();
    void SaveDictionaryWindowState(DictionaryWindowState state);
}
