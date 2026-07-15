namespace Noted.Services
{
    // Coordinates the visibility of windows owned by the application.
    public static class WindowManager
    {
        public static MainWindow? Main { get; set; }
        public static ChecklistWindow? Checklist { get; set; }
        public static DictionaryWindow? Dictionary { get; set; }
        public static ScratchpadWindow? Scratchpad { get; set; }

        internal static Func<ChecklistWindow?>? ChecklistProvider { get; set; }
        internal static Func<DictionaryWindow?>? DictionaryProvider { get; set; }
        internal static Func<ScratchpadWindow?>? ScratchpadProvider { get; set; }

        public static bool AnyWindowVisible()
            => (Main?.IsNotesPanelVisible == true)
            || (Checklist?.IsWindowVisible == true)
            || (Dictionary?.IsWindowVisible == true)
            || (Scratchpad?.IsWindowVisible == true);

        public static void HideAll()
        {
            Main?.HideNotesPanel();
            Checklist?.HideWindow();
            Dictionary?.HideWindow();
            Scratchpad?.HideWindow();
        }

        public static void ShowNotesPanel() => Main?.ShowNotesPanel();

        public static void HideChecklist() => Checklist?.HideWindow();
        public static void HideDictionary() => Dictionary?.HideWindow();
        public static void HideScratchpad() => Scratchpad?.HideWindow();

        public static void ShowChecklistPanel()
        {
            var window = Checklist ?? ChecklistProvider?.Invoke();
            if (window is null)
                return;

            Checklist = window;
            window.ShowWindow();
        }

        public static void ShowDictionaryPanel()
        {
            var window = Dictionary ?? DictionaryProvider?.Invoke();
            if (window is null)
                return;

            Dictionary = window;
            window.ShowWindow();
        }

        public static void ShowScratchpadPanel()
        {
            var window = Scratchpad ?? ScratchpadProvider?.Invoke();
            if (window is null)
                return;

            Scratchpad = window;
            window.ShowWindow();
        }

        public static void ToggleWorkspaceVisibility()
        {
            if (AnyWindowVisible())
                HideAll();
            else
                ShowNotesPanel();
        }

        public static void ToggleChecklist()
        {
            if (Checklist?.IsWindowVisible == true)
                HideChecklist();
            else
                ShowChecklistPanel();
        }

        public static void ToggleDictionary()
        {
            if (Dictionary?.IsWindowVisible == true)
                HideDictionary();
            else
                ShowDictionaryPanel();
        }

        public static void ToggleScratchpad()
        {
            if (Scratchpad?.IsWindowVisible == true)
                HideScratchpad();
            else
                ShowScratchpadPanel();
        }

        public static void ClearAll()
        {
            Main = null;
            Checklist = null;
            Dictionary = null;
            Scratchpad = null;
            ChecklistProvider = null;
            DictionaryProvider = null;
            ScratchpadProvider = null;
        }
    }
}
