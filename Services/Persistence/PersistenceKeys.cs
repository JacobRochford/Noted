namespace Noted.Services;

// keep saved keys stable when feature names or labels change.
internal static class PersistenceKeys
{
    internal const string Notes = "Notes";
    internal const string Checklist = "Checklist";
    internal const string Dictionary = "Dictionary";
    internal const string MiniPad = "MiniPad";
    internal const string NoteEditor = "NoteEditor";
    internal const string JotCapture = "JotCapture";
    internal const string Scratchpad = "Scratchpad";
    internal const string Jots = "Jots";

    internal const string MainWindow = "MainWindow";
    internal const string ChecklistWindow = "ChecklistWindow";
    internal const string DictionaryWindow = "DictionaryWindow";
    internal const string MiniPadWindow = "MiniPadWindow";
    internal const string NoteEditorWindow = "NoteEditorWindow";
    internal const string JotCaptureWindow = "JotCaptureWindow";
    internal const string ScratchpadWindow = "ScratchpadWindow";
    internal const string JotsWindow = "JotsWindow";
    internal const string AccentColorDialog = "AccentColorDialog";
    internal const string AppDialog = "AppDialog";
    internal const string BackupPreviewDialog = "BackupPreviewDialog";
    internal const string ChecklistPriorityColorsDialog = "ChecklistPriorityColorsDialog";
    internal const string NewNoteNameDialog = "NewNoteNameDialog";

    private static readonly IReadOnlyDictionary<HotkeyFeature, string> s_hotkeys =
        new Dictionary<HotkeyFeature, string>
        {
            [HotkeyFeature.Notes] = Notes,
            [HotkeyFeature.Checklist] = Checklist,
            [HotkeyFeature.Dictionary] = Dictionary,
            [HotkeyFeature.MiniPad] = MiniPad,
            [HotkeyFeature.NoteEditor] = NoteEditor,
            [HotkeyFeature.JotCapture] = JotCapture,
            [HotkeyFeature.Scratchpad] = Scratchpad,
            [HotkeyFeature.Jots] = Jots
        };

    internal static string GetHotkey(HotkeyFeature feature) =>
        s_hotkeys.TryGetValue(feature, out var key) ? key : feature.ToString();

    internal static bool TryParseHotkey(string value, out HotkeyFeature feature)
    {
        foreach (var pair in s_hotkeys)
        {
            if (string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                feature = pair.Key;
                return true;
            }
        }

        // keep accepting numeric values and surrounding whitespace as before.
        return Enum.TryParse(value, out feature) && Enum.IsDefined(feature);
    }
}
