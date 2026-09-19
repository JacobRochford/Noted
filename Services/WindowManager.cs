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

        private static NotedWindowVisibility? s_hiddenNotedWindows;

        public static MainWindow? Main { get; internal set; }
        public static ChecklistWindow? Checklist { get; internal set; }
        public static DictionaryWindow? Dictionary { get; internal set; }
        public static ScratchpadWindow? Scratchpad { get; internal set; }
        public static MiniPadWindow? MiniPad { get; internal set; }
        public static NoteEditorWindow? Editor { get; internal set; }

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
            s_hiddenNotedWindows = visibleWindows;

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

            var visibilityToRestore = s_hiddenNotedWindows;
            s_hiddenNotedWindows = null;
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

        internal static bool TryPrepareForShutdown(out string? error)
        {
            try
            {
                if (Editor is not null && !Editor.TryPrepareForClose())
                {
                    CancelPreparedClose();
                    error = null;
                    return false;
                }

                Checklist?.PrepareForApplicationShutdown();
                Dictionary?.PrepareForApplicationShutdown();
                if (Scratchpad is not null &&
                    !Scratchpad.TryPrepareForApplicationShutdown(out var stateError))
                {
                    CancelPreparedClose();
                    error = stateError ?? "Scratchpad window state could not be saved.";
                    return false;
                }
                MiniPad?.PrepareForApplicationShutdown();
            }
            catch (SettingsPersistenceException ex)
            {
                ExceptionDiagnostics.Record(ex);
                CancelPreparedClose();
                error = ex.Message;
                return false;
            }

            error = null;
            return true;
        }

        internal static void CancelPreparedClose()
        {
            Editor?.CancelPreparedClose();
            Checklist?.CancelPreparedClose();
            Dictionary?.CancelPreparedClose();
            Scratchpad?.CancelPreparedClose();
            MiniPad?.CancelPreparedClose();
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
            s_hiddenNotedWindows = null;
        }
    }
}
