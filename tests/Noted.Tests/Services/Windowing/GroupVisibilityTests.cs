using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class GroupVisibilityTests
{
    [TestMethod]
    public void PartialHideLeavesDictionaryDraftVisibleAndTheNextToggleRestoresHiddenWindows() => WithWindows(context =>
    {
        var draft = context.DictionaryModel.Items[0];
        draft.BeginEdit();
        draft.DraftDefinition = "unsaved draft";
        var pad = context.AddPad();
        var checklistState = context.Settings.LoadChecklistWindowState();
        WithWarningsDismissed(WindowManager.HideAll);
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        Assert.IsFalse(context.Dictionary.IsHiddenTogether);
        Assert.IsTrue(draft.HasPendingEdit);
        Assert.IsFalse(context.Checklist.IsWindowVisible);
        Assert.IsTrue(context.Checklist.IsHiddenTogether);
        Assert.IsTrue(pad.IsHiddenTogether);
        Assert.AreEqual(checklistState.ReopenOnStartup, context.Settings.LoadChecklistWindowState().ReopenOnStartup);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Checklist.IsWindowVisible);
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        Assert.IsTrue(pad.IsWindowVisible);
        Assert.IsTrue(draft.HasPendingEdit);
        Assert.IsFalse(context.Checklist.IsHiddenTogether || pad.IsHiddenTogether);
    });

    [TestMethod]
    public void ChecklistSaveFailureDoesNotBlockSafeDictionaryCreationOrRecordTheChecklistAsHidden() => WithWindows(context =>
    {
        var list = (ListBox)context.Checklist.FindName("ItemsList");
        KeyboardTestInput.Press(list, System.Windows.Input.Key.N, System.Windows.Input.ModifierKeys.Control);
        Pump();
        var checklistModel = (ChecklistWindowViewModel)context.Checklist.DataContext;
        var creation = checklistModel.Items.Last();
        creation.Text = "pending checklist";
        creation.IsExpanded = true;
        context.ChecklistService.FailSave = true;
        var entry = context.DictionaryModel.AddItem(beginEdit: true, autoSave: true);
        entry.DraftDefinition = "definition only";
        WithWarningsDismissed(WindowManager.HideAll);
        Assert.IsTrue(context.Checklist.IsWindowVisible);
        Assert.IsFalse(context.Checklist.IsHiddenTogether);
        Assert.IsTrue(creation.IsEditingTitle && creation.IsExpanded);
        Assert.IsTrue(context.Dictionary.IsHiddenTogether);
        Assert.IsFalse(entry.IsAutoSaving || entry.IsEditing || entry.IsExpanded);
        Assert.AreEqual("definition only", context.DictionaryService.Load().Last().Description);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        Assert.IsTrue(context.Checklist.IsWindowVisible);
        Assert.IsTrue(creation.IsEditingTitle);
    });

    [TestMethod]
    public void RepeatedHideAllMergesEarlierHiddenWindowsWithNewlyUnblockedMembers() => WithWindows(context =>
    {
        var draft = context.DictionaryModel.Items[0];
        draft.BeginEdit();
        draft.DraftDefinition = "draft";
        WithWarningsDismissed(WindowManager.HideAll);
        WithWarningsDismissed(WindowManager.HideAll);
        Assert.IsTrue(context.Checklist.IsHiddenTogether);
        draft.CancelEdit();
        WindowManager.HideAll();
        WindowManager.HideAll();
        Assert.IsFalse(WindowManager.AnyWindowVisible());
        Assert.IsTrue(context.Dictionary.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Checklist.IsWindowVisible && context.Dictionary.IsWindowVisible);
    });

    [TestMethod]
    public void IndependentVisibilityChangesAndClosedMiniPadsAreNotRestored() => WithWindows(context =>
    {
        var independentlyHidden = context.AddPad();
        var closed = context.AddPad();
        var neverGrouped = context.AddPad();
        neverGrouped.HideWindow();
        WindowManager.HideAll();
        independentlyHidden.ShowWindow();
        independentlyHidden.HideWindow();
        closed.Close();
        WindowManager.ShowDictionary();
        WindowManager.HideDictionary();
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Checklist.IsWindowVisible);
        Assert.IsFalse(context.Dictionary.IsWindowVisible);
        Assert.IsFalse(independentlyHidden.IsWindowVisible || neverGrouped.IsWindowVisible);
        Assert.IsFalse(WindowManager.MiniPads.Contains(closed));
    });

    [TestMethod]
    public void AClosedUtilityIsNotRestoredThroughItsReplacementInstance() => WithWindows(context =>
    {
        WindowManager.HideAll();
        context.Checklist.Close();
        var replacement = new ChecklistWindow(context.Settings, new ChecklistContentService(context.Directory.Path));
        WindowManager.Checklist = replacement;
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        Assert.IsFalse(replacement.IsVisible);
    });

    [TestMethod]
    public void DictionaryFailedSaveKeepsCreationAndGroupRestoresOnlyTheSuccessfulHides() => WithWindows(context =>
    {
        var entry = context.DictionaryModel.AddItem(beginEdit: true, autoSave: true);
        entry.DraftDefinition = "pending";
        context.DictionaryService.FailSave = true;
        WithWarningsDismissed(WindowManager.HideAll);
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        Assert.IsFalse(context.Dictionary.IsHiddenTogether);
        Assert.IsTrue(entry.IsAutoSaving && entry.IsEditing && entry.IsExpanded);
        Assert.IsTrue(context.Checklist.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Checklist.IsWindowVisible);
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        context.DictionaryService.FailSave = false;
        WindowManager.HideAll();
        Assert.IsFalse(context.Dictionary.IsWindowVisible);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Dictionary.IsWindowVisible);
        Assert.IsFalse(entry.IsAutoSaving || entry.IsEditing || entry.IsExpanded);
    });

    [TestMethod]
    public void ReentrantGroupHotkeysDuringPersistenceDoNotOverwriteTheOutstandingGroup() => WithWindows(context =>
    {
        var entry = context.DictionaryModel.AddItem(beginEdit: true, autoSave: true);
        entry.DraftWord = "word";
        context.DictionaryService.BeforeSave = () =>
        {
            WindowManager.HideAll();
            WindowManager.ToggleNotedWindows();
        };
        WindowManager.HideAll();
        Assert.IsTrue(context.Checklist.IsHiddenTogether && context.Dictionary.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Checklist.IsWindowVisible && context.Dictionary.IsWindowVisible);
        Assert.AreEqual(1, context.DictionaryService.SaveCount);
    });

    [TestMethod]
    public void SavedJotsAndMiniPadsRetainTheirExistingRestorationAndClosedInstanceBehavior() => WithWindows(context =>
    {
        var content = new JotsContentService(context.Directory.Path);
        content.SaveItems([new JotItemState { Title = "first" }, new JotItemState { Title = "second" }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        var collection = new JotsWindow(context.Settings, workspace);
        WindowManager.Jots = collection;
        WindowManager.JotProvider = id => new JotsWindow(context.Settings, workspace, workspace.Items.Single(item => item.Id == id));
        try
        {
            collection.Show();
            foreach (var item in workspace.Items) WindowManager.ShowJot(item.Id);
            var first = WindowManager.JotWindows.First();
            var second = WindowManager.JotWindows.Last();
            var pad = context.AddPad();
            pad.WindowState = WindowState.Maximized;
            WindowManager.HideAll();
            second.Close();
            WindowManager.ToggleNotedWindows();
            Assert.IsTrue(collection.IsWindowVisible && first.IsWindowVisible && pad.IsWindowVisible);
            Assert.AreEqual(WindowState.Maximized, pad.WindowState);
            Assert.HasCount(1, WindowManager.JotWindows);
        }
        finally
        {
            foreach (var window in WindowManager.JotWindows) window.Close();
            collection.Close();
            WindowManager.Jots = null;
        }
    });

    [TestMethod]
    public void NoteEditorRestoresItsWindowStateAndHonorsIndependentHiding() => WithWindows(context =>
    {
        var editor = context.AddEditor();
        editor.Show();
        editor.WindowState = WindowState.Maximized;
        WindowManager.HideAll();
        Assert.IsTrue(editor.IsHiddenTogether);
        Assert.AreEqual(WindowState.Minimized, editor.WindowState);
        WindowManager.ToggleNotedWindows();
        Assert.AreEqual(WindowState.Maximized, editor.WindowState);
        Assert.IsFalse(editor.IsHiddenTogether);
        WindowManager.HideAll();
        editor.HideWindow();
        WindowManager.ToggleNotedWindows();
        Assert.IsFalse(editor.IsWindowVisible);
        Assert.IsTrue(context.Checklist.IsWindowVisible);
    });

    [TestMethod]
    public void ModalEditorStaysVisibleWhileSafeUtilitiesHideAndRestore() => WithWindows(context =>
    {
        var editor = context.AddEditor();
        editor.Show();
        var dialog = new Window { Owner = editor, Width = 180, Height = 100, Content = new TextBlock { Text = "modal" } };
        Exception? failure = null;
        var operation = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            try
            {
                Assert.IsFalse(editor.CanChangeGroupVisibility);
                WindowManager.HideAll();
                Assert.IsTrue(editor.IsWindowVisible);
                Assert.IsFalse(editor.IsHiddenTogether);
                Assert.IsFalse(context.Checklist.IsWindowVisible || context.Dictionary.IsWindowVisible);
                WindowManager.ToggleNotedWindows();
                Assert.IsTrue(context.Checklist.IsWindowVisible && context.Dictionary.IsWindowVisible);
                Assert.IsTrue(editor.IsWindowVisible);
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        try { dialog.ShowDialog(); }
        finally { operation.Abort(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });

    [TestMethod]
    public void NotesPanelIndependentShowAndHideCancelsOnlyItsGroupMembership() => WithWindows(context =>
    {
        var main = context.AddMain();
        main.ShowNotesPanel();
        WindowManager.HideAll();
        Assert.IsFalse(main.IsNotesPanelVisible);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(main.IsNotesPanelVisible);
        WindowManager.HideAll();
        main.ShowNotesPanel();
        main.HideNotesPanel();
        WindowManager.ToggleNotedWindows();
        Assert.IsFalse(main.IsNotesPanelVisible);
        Assert.IsTrue(context.Checklist.IsWindowVisible && context.Dictionary.IsWindowVisible);
    });

    [TestMethod]
    public void MinimizingDuringAFailedGuardDoesNotMarkTheDictionaryAsGroupHidden() => WithWindows(context =>
    {
        var entry = context.DictionaryModel.AddItem(beginEdit: true, autoSave: true);
        entry.DraftWord = "pending";
        context.DictionaryService.FailSave = true;
        context.DictionaryService.BeforeSave = () => context.Dictionary.WindowState = WindowState.Minimized;
        WithWarningsDismissed(WindowManager.HideAll);
        Assert.AreEqual(WindowState.Minimized, context.Dictionary.WindowState);
        Assert.IsTrue(context.Dictionary.IsVisible);
        Assert.IsFalse(context.Dictionary.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(context.Checklist.IsWindowVisible);
        Assert.AreEqual(WindowState.Minimized, context.Dictionary.WindowState);
        Assert.IsFalse(context.Dictionary.IsHiddenTogether);
        Assert.IsTrue(entry.IsAutoSaving && entry.IsEditing);
    });

    [TestMethod]
    [DataRow("hide")]
    [DataRow("close")]
    [DataRow("minimize")]
    public void IndependentVisibilityChangesDuringAGroupWarningAreNotAddedToTheHiddenGroup(string change) => WithWindows(context =>
    {
        var draft = context.DictionaryModel.Items[0];
        draft.BeginEdit();
        draft.DraftDefinition = "blocking draft";
        var changed = context.AddPad();
        var grouped = context.AddPad();
        var editor = context.AddEditor();
        editor.Show();
        WithWarningAction(() =>
        {
            if (change == "close") changed.Close();
            else if (change == "minimize") changed.WindowState = WindowState.Minimized;
            else changed.HideWindow();
            editor.HideWindow();
        }, WindowManager.HideAll);
        Assert.IsFalse(changed.IsHiddenTogether || editor.IsHiddenTogether);
        Assert.IsTrue(grouped.IsHiddenTogether && context.Checklist.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(grouped.IsWindowVisible && context.Checklist.IsWindowVisible);
        Assert.IsTrue(context.Dictionary.IsWindowVisible && draft.HasPendingEdit);
        Assert.IsFalse(changed.IsWindowVisible || editor.IsWindowVisible);
        if (change == "close") Assert.IsFalse(WindowManager.MiniPads.Contains(changed));
        if (change == "minimize") Assert.AreEqual(WindowState.Minimized, changed.WindowState);
    });

    [TestMethod]
    public void AReplacementUtilityShownDuringAGroupWarningIsNotHiddenThroughTheOldInstance() => WithWindows(context =>
    {
        KeyboardTestInput.Press((ListBox)context.Checklist.FindName("ItemsList"),
            System.Windows.Input.Key.N, System.Windows.Input.ModifierKeys.Control);
        Pump();
        ((ChecklistWindowViewModel)context.Checklist.DataContext).Items.Last().Text = "pending";
        context.ChecklistService.FailSave = true;
        DictionaryWindow? replacement = null;
        var pad = context.AddPad();
        WithWarningAction(() =>
        {
            context.Dictionary.Close();
            replacement = new DictionaryWindow(context.Settings, context.DictionaryService);
            WindowManager.Dictionary = replacement;
            replacement.Show();
        }, WindowManager.HideAll);
        Assert.IsNotNull(replacement);
        Assert.IsTrue(replacement.IsWindowVisible);
        Assert.IsFalse(replacement.IsHiddenTogether);
        Assert.IsTrue(pad.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsTrue(replacement.IsWindowVisible && pad.IsWindowVisible);
    });

    [TestMethod]
    public void NotesPanelIndependentShowAndHideDuringAGroupWarningCancelsItsNewGroupMembership() => WithWindows(context =>
    {
        var main = context.AddMain();
        main.ShowNotesPanel();
        var draft = context.DictionaryModel.Items[0];
        draft.BeginEdit();
        draft.DraftDefinition = "blocking draft";
        WithWarningAction(() =>
        {
            main.ShowNotesPanel();
            main.HideNotesPanel();
        }, WindowManager.HideAll);
        Assert.IsFalse(main.IsNotesPanelVisible);
        Assert.IsTrue(context.Checklist.IsHiddenTogether);
        WindowManager.ToggleNotedWindows();
        Assert.IsFalse(main.IsNotesPanelVisible);
        Assert.IsTrue(context.Checklist.IsWindowVisible && context.Dictionary.IsWindowVisible);
    });

    private static void WithWarningAction(Action duringWarning, Action action)
    {
        Exception? failure = null;
        var ran = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialogs = Application.Current.Windows.OfType<AppDialog>().ToArray();
            if (dialogs.Length == 0) return;
            try
            {
                if (!ran)
                {
                    ran = true;
                    duringWarning();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var dialog in dialogs) dialog.Close();
            }
        };
        timer.Start();
        try { action(); }
        finally { timer.Stop(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.IsTrue(ran, "The visibility change must run while the warning is open.");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void WithWarningsDismissed(Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<AppDialog>().ToArray()) dialog.Close();
        };
        timer.Start();
        try { action(); }
        finally { timer.Stop(); }
    }

    private static void WithWindows(Action<Context> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var original = Application.Current.Windows.Cast<Window>().ToHashSet();
        WindowManager.ClearAll();
        var context = new Context(directory);
        try { action(context); }
        finally
        {
            context.DictionaryService.FailSave = false;
            context.DictionaryService.BeforeSave = null;
            context.ChecklistService.FailSave = false;
            foreach (var item in context.DictionaryModel.Items.Where(item => item.HasPendingEdit).ToArray()) item.CancelEdit();
            foreach (var window in Application.Current.Windows.Cast<Window>().Where(window => !original.Contains(window)).ToArray())
            {
                if (window is MainWindow main) main.CleanupResources();
                if (window is NoteEditorWindow editor)
                {
                    editor.CloseAllDocuments();
                    editor.TryPrepareForClose();
                }
                window.Close();
            }
            context.Files?.Dispose();
            WindowManager.ClearAll();
            Pump();
        }
    });

    private sealed class Context
    {
        internal TemporaryTestDirectory Directory { get; }
        internal AppSettingsService Settings { get; }
        internal ChecklistWindow Checklist { get; }
        internal DictionaryWindow Dictionary { get; }
        internal DictionaryWindowViewModel DictionaryModel => (DictionaryWindowViewModel)Dictionary.DataContext;
        internal DictionaryKeyboardTests.RecordingService DictionaryService { get; }
        internal ChecklistService ChecklistService { get; }
        internal NoteFileService? Files { get; private set; }

        internal Context(TemporaryTestDirectory directory)
        {
            Directory = directory;
            Settings = new AppSettingsService(directory.Path);
            ChecklistService = new ChecklistService(directory.Path);
            DictionaryService = new DictionaryKeyboardTests.RecordingService(directory.Path);
            Checklist = new ChecklistWindow(Settings, ChecklistService);
            Dictionary = new DictionaryWindow(Settings, DictionaryService);
            WindowManager.Checklist = Checklist;
            WindowManager.Dictionary = Dictionary;
            Checklist.Show();
            Dictionary.Show();
            Pump();
        }

        internal MiniPadWindow AddPad()
        {
            var pad = new MiniPadWindow(new MiniPadRecoveryService(Directory.Path, Guid.NewGuid()), Settings);
            WindowManager.RegisterMiniPad(pad);
            pad.Show();
            return pad;
        }

        internal NoteEditorWindow AddEditor()
        {
            var editor = new NoteEditorWindow(new NoteContentService(Directory.Path), Settings,
                new NoteRecoveryService(Directory.Path), new NoteEditorSessionService(Directory.Path));
            WindowManager.Editor = editor;
            return editor;
        }

        internal MainWindow AddMain()
        {
            Settings.SaveNotesDirectory(Directory.File("notes"));
            Files = new NoteFileService(Settings);
            var editor = AddEditor();
            var main = new MainWindow(Settings, Files, editor, new NoteOperations(Files, editor.Workspace), new StartupService(),
                new BackupCoordinator(new FullBackupService(Directory.Path, Files.NotesDirectory),
                    new ShutdownFlushCoordinator(), () => Files.NotesDirectory), (_, _) => { });
            WindowManager.Main = main;
            WithWarningsDismissed(() =>
            {
                main.Show();
                Pump();
            });
            return main;
        }
    }

    private sealed class StartupService : IRunOnStartupService
    {
        public bool IsRunOnStartupEnabled => false;
        public (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled) =>
            throw new AssertFailedException("Group visibility must not change startup integration.");
    }

    private sealed class ChecklistService : IChecklistContentService
    {
        private readonly ChecklistContentService _store;
        internal bool FailSave { get; set; }
        internal ChecklistService(string path)
        {
            _store = new ChecklistContentService(path);
            _store.SaveItems([new ChecklistItemState { Text = "task" }]);
        }
        public ChecklistContentLoadResult LoadItems() => _store.LoadItems();
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => _store.SaveTabs(tabs);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            if (FailSave) throw new IOException("Simulated checklist save failure.");
            return _store.SaveItems(items);
        }
    }
}
