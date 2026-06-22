namespace Noted.Services
{
    // Handles the visibility and state of all windows in the application
    public static class WindowManager
    {
        public static MainWindow? Main { get; set; }
        public static ChecklistWindow? Checklist { get; set; }
        public static DictionaryWindow? Dictionary { get; set; }

        // true if any panels are visible
        public static bool AnyWindowVisible()
            => (Main?.IsNotesPanelVisible == true)
            || (Checklist?.IsWindowVisible == true)
            || (Dictionary?.IsWindowVisible == true);

        public static void HideAll()
        {
            Main?.HideNotesPanel();
            Checklist?.HideWindow();
            Dictionary?.HideWindow();
        }

        public static void ShowNotesPanel()
        {
            Main?.ShowNotesPanel();
        }
        
        public static void ShowDictionaryPanel()
        {
            Dictionary?.ShowWindow();
        }
        public static void ShowChecklistPanel()
        {
            Checklist?.ShowWindow();
        }

        public static void ToggleAll()
        {
            if (AnyWindowVisible())
                HideAll();
            else
                ShowNotesPanel();
        }
    }
}
