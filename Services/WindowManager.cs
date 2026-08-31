namespace Noted.Services
{
    // Coordinates the visibility of windows owned by the application.
    public static class WindowManager
    {
        private sealed record NotedWindowVisibility(
            bool Notes,
            bool Checklist,
            bool Dictionary,
            bool Scratchpad,
            bool MiniPad,
            bool Editor);

        private static NotedWindowVisibility? _hiddenNotedWindows;

        public static MainWindow? Main { get; set; }
        public static ChecklistWindow? Checklist { get; set; }
        public static DictionaryWindow? Dictionary { get; set; }
        public static ScratchpadWindow? Scratchpad { get; set; }
        public static MiniPadWindow? MiniPad { get; set; }
        public static NoteEditorWindow? Editor { get; set; }

        internal static Func<ChecklistWindow?>? ChecklistProvider { get; set; }
        internal static Func<DictionaryWindow?>? DictionaryProvider { get; set; }
        internal static Func<ScratchpadWindow?>? ScratchpadProvider { get; set; }
        internal static Func<MiniPadWindow?>? MiniPadProvider { get; set; }

        public static bool AnyWindowVisible()
            => (Main?.IsNotesPanelVisible == true)
            || (Checklist?.IsWindowVisible == true)
            || (Dictionary?.IsWindowVisible == true)
            || (Scratchpad?.IsWindowVisible == true)
            || (MiniPad?.IsWindowVisible == true)
            || (Editor?.IsWindowVisible == true);

        public static void HideAll()
        {
            var visibleWindows = new NotedWindowVisibility(
                Main?.IsNotesPanelVisible == true,
                Checklist?.IsWindowVisible == true,
                Dictionary?.IsWindowVisible == true,
                Scratchpad?.IsWindowVisible == true,
                MiniPad?.IsWindowVisible == true,
                Editor?.IsWindowVisible == true);
            _hiddenNotedWindows = visibleWindows;

            if (visibleWindows.Notes)
                Main?.HideNotesPanel();
            if (visibleWindows.Checklist)
                Checklist?.HideTogether();
            if (visibleWindows.Dictionary)
                Dictionary?.HideTogether();
            if (visibleWindows.Scratchpad)
                Scratchpad?.HideTogether();
            if (visibleWindows.MiniPad)
                MiniPad?.HideTogether();
            if (visibleWindows.Editor)
                Editor?.HideTogether();
        }

        public static void ShowNotesPanel()
        {
            Main?.ShowNotesPanel();
        }

        public static void HideChecklist() => Checklist?.HideWindow();
        public static void HideDictionary() => Dictionary?.HideWindow();
        public static void HideScratchpad() => Scratchpad?.HideWindow();
        public static void HideMiniPad() => MiniPad?.HideWindow();
        public static void HideEditor() => Editor?.HideWindow();

        public static void ShowChecklist()
        {
            var window = Checklist ?? ChecklistProvider?.Invoke();
            if (window is null)
                return;

            Checklist = window;
            window.ShowWindow();
        }

        public static void ShowDictionary()
        {
            var window = Dictionary ?? DictionaryProvider?.Invoke();
            if (window is null)
                return;

            Dictionary = window;
            window.ShowWindow();
        }

        public static void ShowScratchpad()
        {
            var window = Scratchpad ?? ScratchpadProvider?.Invoke();
            if (window is null)
                return;

            Scratchpad = window;
            window.ShowWindow();
        }

        public static void ShowMiniPad()
        {
            var window = MiniPad ?? MiniPadProvider?.Invoke();
            if (window is null)
                return;

            MiniPad = window;
            window.ShowWindow();
        }

        public static void ToggleNotedWindows()
        {
            if (AnyWindowVisible())
            {
                HideAll();
                return;
            }

            var visibilityToRestore = _hiddenNotedWindows;
            _hiddenNotedWindows = null;
            if (visibilityToRestore is null)
            {
                ShowNotesPanel();
                return;
            }

            if (visibilityToRestore.Notes)
                Main?.ShowNotesPanel();
            if (visibilityToRestore.Checklist)
                Checklist?.RestoreTogether();
            if (visibilityToRestore.Dictionary)
                Dictionary?.RestoreTogether();
            if (visibilityToRestore.Scratchpad)
                Scratchpad?.RestoreTogether();
            if (visibilityToRestore.MiniPad)
                MiniPad?.RestoreTogether();
            if (visibilityToRestore.Editor)
                Editor?.RestoreTogether();
        }

        public static void ToggleChecklist()
        {
            if (Checklist?.IsWindowVisible == true)
                HideChecklist();
            else
                ShowChecklist();
        }

        public static void ToggleDictionary()
        {
            if (Dictionary?.IsWindowVisible == true)
                HideDictionary();
            else
                ShowDictionary();
        }

        public static void ToggleScratchpad()
        {
            if (Scratchpad?.IsWindowVisible == true)
                HideScratchpad();
            else
                ShowScratchpad();
        }

        public static void ToggleMiniPad()
        {
            if (MiniPad?.IsWindowVisible == true)
                HideMiniPad();
            else
                ShowMiniPad();
        }

        public static void ClearAll()
        {
            Main = null;
            Checklist = null;
            Dictionary = null;
            Scratchpad = null;
            MiniPad = null;
            Editor = null;
            ChecklistProvider = null;
            DictionaryProvider = null;
            ScratchpadProvider = null;
            MiniPadProvider = null;
            _hiddenNotedWindows = null;
        }
    }
}
