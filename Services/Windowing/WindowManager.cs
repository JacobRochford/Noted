using System.Windows.Interop;
using Noted.Helpers;

namespace Noted.Services
{
    // Coordinates application window visibility and editor activation priority.
    public static class WindowManager
    {
        private sealed record NotedWindowVisibility(
            bool Notes,
            bool Checklist,
            bool Dictionary,
            bool Jots,
            IReadOnlyList<Guid> JotWindows,
            bool Scratchpad,
            IReadOnlyList<Guid> MiniPads,
            bool Editor);

        private static NotedWindowVisibility? s_hiddenNotedWindows;
        public const int MaximumMiniPads = 5;
        private static readonly List<MiniPadWindow> s_miniPads = [];
        public static IReadOnlyList<MiniPadWindow> MiniPads => s_miniPads.ToArray();

        public static MainWindow? Main { get; internal set; }
        public static ChecklistWindow? Checklist { get; internal set; }
        public static DictionaryWindow? Dictionary { get; internal set; }
        public static JotsWindow? Jots { get; internal set; }
        private static readonly Dictionary<Guid, JotsWindow> s_jotWindows = [];
        internal static IReadOnlyList<JotsWindow> JotWindows => s_jotWindows.Values.ToArray();
        internal static JotCaptureWindow? JotCapture { get; set; }
        internal static Func<JotCaptureWindow?>? JotCaptureProvider { get; set; }
        internal static Func<Guid, JotsWindow?>? JotProvider { get; set; }

        public static void ShowJotCapture()
        {
            JotCapture ??= JotCaptureProvider?.Invoke();
            JotCapture?.ShowCapture();
        }

        public static void ShowJot(Guid id)
        {
            if (!s_jotWindows.TryGetValue(id, out var window))
            {
                window = JotProvider?.Invoke(id);
                if (window is null)
                    return;
                s_jotWindows.Add(id, window);
                window.Closed += SavedJot_Closed;
            }
            window.ShowWindow();
        }

        private static void SavedJot_Closed(object? sender, EventArgs e)
        {
            if (sender is JotsWindow { JotId: { } id } window)
            {
                window.Closed -= SavedJot_Closed;
                if (s_jotWindows.GetValueOrDefault(id) == window)
                    s_jotWindows.Remove(id);
            }
        }
        public static ScratchpadWindow? Scratchpad { get; internal set; }
        public static MiniPadWindow? MiniPad => s_miniPads.LastOrDefault();
        public static NoteEditorWindow? Editor { get; internal set; }

        internal static Func<ChecklistWindow?>? ChecklistProvider { get; set; }
        internal static Func<DictionaryWindow?>? DictionaryProvider { get; set; }
        internal static Func<JotsWindow?>? JotsProvider { get; set; }
        internal static Func<ScratchpadWindow?>? ScratchpadProvider { get; set; }
        internal static Func<MiniPadWindow?>? MiniPadProvider { get; set; }

        internal static void RegisterMiniPad(MiniPadWindow window)
        {
            if (s_miniPads.Contains(window)) return;
            if (s_miniPads.Count >= MaximumMiniPads || s_miniPads.Any(pad => pad.InstanceId == window.InstanceId))
                throw new InvalidOperationException("MiniPad limit reached or duplicate instance ID.");
            s_miniPads.Insert(0, window);
            window.Activated += MiniPadActivated;
            window.Closed += MiniPadClosed;
        }

        private static void MiniPadActivated(object? sender, EventArgs e)
        {
            if (sender is MiniPadWindow window && s_miniPads.Remove(window))
                s_miniPads.Add(window);
        }

        private static void MiniPadClosed(object? sender, EventArgs e)
        {
            if (sender is not MiniPadWindow window) return;
            window.Activated -= MiniPadActivated;
            window.Closed -= MiniPadClosed;
            s_miniPads.Remove(window);
        }

        internal static IReadOnlyList<MiniPadRecoveryIssue> RestoreMiniPads(
            IAppSettingsService settings,
            Func<IMiniPadRecoveryService, MiniPadRecoveryDraft?, MiniPadWindow> createWindow)
        {
            var issues = new List<MiniPadRecoveryIssue>();
            foreach (var (id, state) in settings.LoadMiniPadWindows()
                         .Where(entry => entry.Key != Guid.Empty && !entry.Value.IsClosed)
                         .OrderByDescending(entry => entry.Value.ReopenOnStartup))
            {
                if (s_miniPads.Count >= MaximumMiniPads) break;
                var recovery = new MiniPadRecoveryService(settings.AppDataDirectory, id);
                var result = recovery.LoadDraft();
                issues.AddRange(result.Issues);
                if (result.Draft is null && !state.ReopenOnStartup && result.Issues.Count == 0)
                    continue;
                var window = createWindow(recovery, result.Draft);
                RegisterMiniPad(window);
                if (state.ReopenOnStartup || result.Issues.Count > 0)
                    window.Show();
            }
            return issues;
        }

        internal static void ConfigureEditorActivation(NoteEditorWindow editor)
        {
            editor.Activated += EditorActivationChanged;
            editor.Deactivated += EditorActivationChanged;
        }

        private static void EditorActivationChanged(object? sender, EventArgs e)
        {
            if (sender is not NoteEditorWindow editor)
                return;

            // Apply priority synchronously to each editor's own activation state.
            editor.Topmost = editor.IsActive;
            if (editor.IsActive)
                return;

            var handle = new WindowInteropHelper(editor).Handle;
            var foreground = WindowInterop.GetForegroundWindow();
            if (handle == IntPtr.Zero || foreground == IntPtr.Zero || foreground == handle)
                return;

            // HWND_NOTOPMOST places the editor above all normal windows. Move it
            // behind the foreground window without activating or moving that window.
            // Inserting after a topmost window could promote the editor again.
            if ((WindowInterop.GetWindowLong(foreground, WindowInterop.GWL_EXSTYLE) &
                 WindowInterop.WS_EX_TOPMOST) != 0)
                return;

            WindowInterop.SetWindowPos(handle, foreground, 0, 0, 0, 0,
                WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE |
                WindowInterop.SWP_NOACTIVATE | WindowInterop.SWP_NOOWNERZORDER);
        }

        public static bool AnyWindowVisible()
            => (Main?.IsNotesPanelVisible == true)
            || (Checklist?.IsWindowVisible == true)
            || (Dictionary?.IsWindowVisible == true)
            || (Jots?.IsWindowVisible == true)
            || JotWindows.Any(window => window.IsWindowVisible)
            || (Scratchpad?.IsWindowVisible == true)
            || s_miniPads.Any(window => window.IsWindowVisible)
            || (Editor?.IsWindowVisible == true);

        public static void HideAll()
        {
            if (Editor?.CanChangeGroupVisibility == false)
                return;

            var visibleWindows = new NotedWindowVisibility(
                Main?.IsNotesPanelVisible == true,
                Checklist?.IsWindowVisible == true,
                Dictionary?.IsWindowVisible == true,
                Jots?.IsWindowVisible == true,
                JotWindows.Where(window => window.IsWindowVisible).Select(window => window.JotId!.Value).ToArray(),
                Scratchpad?.IsWindowVisible == true,
                s_miniPads.Where(window => window.IsWindowVisible).Select(window => window.InstanceId).ToArray(),
                Editor?.IsWindowVisible == true);
            s_hiddenNotedWindows = visibleWindows;

            if (visibleWindows.Notes)
                Main?.HideNotesPanel();
            if (visibleWindows.Checklist)
                Checklist?.HideTogether();
            if (visibleWindows.Dictionary)
                Dictionary?.HideTogether();
            if (visibleWindows.Jots)
                Jots?.HideTogether();
            foreach (var window in JotWindows.Where(window => visibleWindows.JotWindows.Contains(window.JotId!.Value)))
                window.HideTogether();
            if (visibleWindows.Scratchpad)
                Scratchpad?.HideTogether();
            foreach (var window in MiniPads.Where(window => visibleWindows.MiniPads.Contains(window.InstanceId)))
                window.HideTogether();
            if (visibleWindows.Editor)
                Editor?.HideTogether();
        }

        public static void ShowNotesPanel()
        {
            Main?.ShowNotesPanel();
        }

        internal static void RestoreWindowPositions()
        {
            if (Editor?.CanChangeGroupVisibility == false)
                return;

            // Include minimized windows, but leave intentionally hidden utilities closed.
            if (Checklist?.IsVisible == true)
                Checklist.ShowWindow();
            if (Dictionary?.IsVisible == true)
                Dictionary.ShowWindow();
            if (Jots?.IsVisible == true)
                Jots.ShowWindow();
            foreach (var window in JotWindows.Where(window => window.IsVisible))
                window.ShowWindow();
            if (Scratchpad?.IsVisible == true)
                Scratchpad.ShowWindow();
            foreach (var window in MiniPads.Where(window => window.IsVisible))
                window.ShowWindow();
            if (Editor?.IsVisible == true)
                Editor.ShowWindow();
            Main?.RestoreNotesPosition();
        }

        public static void HideChecklist() => Checklist?.HideWindow();
        public static void HideDictionary() => Dictionary?.HideWindow();
        public static void HideJots() => Jots?.HideWindow();
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

        public static void ShowJots()
        {
            var window = Jots ?? JotsProvider?.Invoke();
            if (window is null)
                return;

            Jots = window;
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

            RegisterMiniPad(window);
            window.ShowWindow();
        }

        public static void NewMiniPad()
        {
            if (s_miniPads.Count >= MaximumMiniPads)
            {
                ShowMiniPad();
                return;
            }
            var window = MiniPadProvider?.Invoke();
            if (window is null) return;
            RegisterMiniPad(window);
            window.ShowWindow();
        }

        public static void ShowAllMiniPads()
        {
            foreach (var window in MiniPads)
                window.ShowWindow();
        }

        public static void ToggleNotedWindows()
        {
            if (Editor?.CanChangeGroupVisibility == false)
                return;

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
            if (visibilityToRestore.Jots)
                Jots?.RestoreTogether();
            foreach (var window in JotWindows.Where(window => visibilityToRestore.JotWindows.Contains(window.JotId!.Value)))
                window.RestoreTogether();
            if (visibilityToRestore.Scratchpad)
                Scratchpad?.RestoreTogether();
            foreach (var window in MiniPads.Where(window => visibilityToRestore.MiniPads.Contains(window.InstanceId)))
                window.RestoreTogether();
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

        public static void ToggleJots()
        {
            if (Jots?.IsWindowVisible == true)
                HideJots();
            else
                ShowJots();
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
            ShowMiniPad();
        }

        public static void ToggleEditor()
        {
            if (Editor?.CanChangeGroupVisibility == false)
                return;

            if (Editor?.IsWindowVisible == true)
                HideEditor();
            else
                Editor?.ShowWindow();
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
                Jots?.PrepareForApplicationShutdown();
                foreach (var window in JotWindows)
                    window.PrepareForApplicationShutdown();
                if (Scratchpad is not null &&
                    !Scratchpad.TryPrepareForApplicationShutdown(out var stateError))
                {
                    CancelPreparedClose();
                    error = stateError ?? "Scratchpad window state could not be saved.";
                    return false;
                }
                foreach (var window in MiniPads)
                    window.PrepareForApplicationShutdown();
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
            Jots?.CancelPreparedClose();
            foreach (var window in JotWindows)
                window.CancelPreparedClose();
            Scratchpad?.CancelPreparedClose();
            foreach (var window in MiniPads)
                window.CancelPreparedClose();
        }

        public static void ClearAll()
        {
            Main = null;
            Checklist = null;
            Dictionary = null;
            Jots = null;
            foreach (var window in JotWindows)
                window.Closed -= SavedJot_Closed;
            s_jotWindows.Clear();
            JotCapture = null;
            JotCaptureProvider = null;
            JotProvider = null;
            Scratchpad = null;
            foreach (var window in MiniPads)
                MiniPadClosed(window, EventArgs.Empty);
            Editor = null;
            ChecklistProvider = null;
            DictionaryProvider = null;
            JotsProvider = null;
            ScratchpadProvider = null;
            MiniPadProvider = null;
            s_hiddenNotedWindows = null;
        }
    }
}
