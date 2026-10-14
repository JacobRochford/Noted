using System.IO;

namespace Noted.Services;

internal static class PersistencePaths
{
    internal const string AppDirectory = "Noted";
    internal const string DefaultNotesDirectory = "Notes";
    internal const string SettingsFile = "settings.json";
    internal const string SettingsHistoryDirectory = "backups";
    internal const string ChecklistFile = "checklist.json";
    internal const string ChecklistTabsFile = "checklist-tabs.json";
    internal const string DictionaryFile = "dictionary.json";
    internal const string JotsFile = "jots.json";
    internal const string JotCaptureDraftFile = "jot-capture-draft.json";
    internal const string JotCaptureConflictsDirectory = "quick-jot-conflicts";
    internal const string ScratchpadFile = "scratchpad.rtf";
    internal const string SessionDirectory = "session";
    internal const string EditorSessionFile = "editor-workspace.json";
    internal const string RecoveryDirectory = "recovery";
    internal const string LegacyEditorDraftFile = "editor-draft.json";
    internal const string NoteHistoryDirectory = "note-history";
    internal const string DeletedNotesDirectory = "DeletedNotes";
    internal const string ArchivedNotesDirectory = ".archive";

    internal const string AppBackupRoot = "app";
    internal const string NotesBackupRoot = "notes";
    internal const string AppBackupPrefix = AppBackupRoot + "/";
    internal const string NotesBackupPrefix = NotesBackupRoot + "/";
    internal const string RecoveryBackupPrefix = AppBackupPrefix + RecoveryDirectory + "/";
    internal const string NoteHistoryBackupPrefix = AppBackupPrefix + NoteHistoryDirectory + "/";
    internal const string DeletedNotesBackupPrefix = AppBackupPrefix + DeletedNotesDirectory + "/";

    internal static string DefaultAppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDirectory);
}
