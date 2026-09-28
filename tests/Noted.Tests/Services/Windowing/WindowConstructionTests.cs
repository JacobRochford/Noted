using System.Runtime.ExceptionServices;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Search;
using System.Windows.Documents;
using Noted.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.ViewModels;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class WindowConstructionTests
{
    [TestMethod]
    public void SharedResourcesAndChangedWindowsLoadWithoutStartingTheApplication()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var directory = new TemporaryTestDirectory();
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(App).TypeHandle);
                VerifyRenderingDuringDocumentChanges();
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var settings = new AppSettingsService(directory.Path);
                var editor = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
                    new NoteRecoveryService(directory.Path), new NoteEditorSessionService(directory.Path));
                var miniPad = new MiniPadWindow(new MiniPadRecoveryService(directory.Path), settings);
                var dictionary = new DictionaryWindow(settings, new DictionaryContentService(directory.Path));
                var checklist = new ChecklistWindow(settings, new ChecklistContentService(directory.Path));
                var scratchpad = new ScratchpadWindow(new ScratchpadWindowViewModel(settings, new ScratchpadContentService(directory.Path)));
                settings.SaveNotesDirectory(directory.File("notes"));
                using var files = new NoteFileService(settings);
                var backupCoordinator = new BackupCoordinator(
                    new FullBackupService(directory.Path, files.NotesDirectory),
                    new ShutdownFlushCoordinator(),
                    () => files.NotesDirectory);
                var noteOperations = new NoteOperations(files, editor.Workspace);
                var main = new MainWindow(settings, files, editor, noteOperations, new ConstructionStartupService(),
                    backupCoordinator,
                    (_, _) => Assert.Fail("Construction must not change the theme.")) { Width = 1280, Height = 900 };
                foreach (var window in new Window[] { editor, miniPad, dictionary, checklist, scratchpad, main })
                {
                    // Build templates and bindings without displaying a window or loading user data.
                    window.Measure(new Size(window.Width, window.Height));
                    window.Arrange(new Rect(0, 0, window.Width, window.Height));
                    window.UpdateLayout();
                }
                var settingsView = (SettingsView)main.FindName("SettingsView");
                settingsView.Visibility = Visibility.Visible;
                settingsView.Measure(new Size(430, 900));
                settingsView.Arrange(new Rect(0, 0, 430, 900));
                settingsView.UpdateLayout();
                var settingsModel = (SettingsViewModel)settingsView.DataContext;
                Assert.AreEqual("No full backup has been created yet.",
                    ((TextBlock)settingsView.FindName("BackupStatusText")).Text);
                Assert.AreSame(main.FindResource("HeaderChromeButtonStyle"),
                    FindHotkeyEditButton(settingsView)!.Style);
                Assert.AreEqual(settingsModel.ThemeMode,
                    ((ComboBox)settingsView.FindName("AppearanceModeCombo")).SelectedValue);
                VerifySettingsBindings(settingsView, settings, files);
                VerifyFindLifecycle(editor, miniPad, scratchpad, directory);
                main.CleanupResources();
                main.Close();
                VerifyShutdownPreparation(editor, scratchpad, directory);
                miniPad.KeepDraftOnClose();
                editor.Close();
                miniPad.Close();
                dictionary.Close();
                checklist.Close();
                scratchpad.Close();
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Window construction did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyFindLifecycle(
        NoteEditorWindow noteEditor, MiniPadWindow miniPad, ScratchpadWindow scratchpad,
        TemporaryTestDirectory directory)
    {
        const string text = "foo xx foo xx foo";
        var path = directory.File("find-lifecycle.txt");
        File.WriteAllText(path, text);
        var document = new OpenNoteDocument(path, text, text, isDirty: false);
        noteEditor.Workspace.Add(document);
        Invoke(noteEditor, "ActivateDocument", document, false);
        ((TextBox)miniPad.FindName("Editor")).Text = text;
        var richEditor = (RichTextBox)scratchpad.FindName("Editor");
        new TextRange(richEditor.Document.ContentStart, richEditor.Document.ContentEnd).Text = text;

        foreach (var window in new Window[] { miniPad, noteEditor, scratchpad })
        {
            var input = (TextBox)window.FindName("FindTextBox");
            var session = (TextSearchSession)Field(window, "_findSession");
            void Open()
            {
                Invoke(window, "OpenFind", window is NoteEditorWindow ? [false] : []);
                if (window is ScratchpadWindow)
                    input.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            }
            void Close() => Invoke(window, "CloseFind", window is NoteEditorWindow ? [false] : []);
            void Select(int start, int length)
            {
                if (window is ScratchpadWindow)
                {
                    var beginning = richEditor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward)!;
                    richEditor.Selection.Select(beginning.GetPositionAtOffset(start)!,
                        beginning.GetPositionAtOffset(start + length)!);
                }
                else
                {
                    var editor = (TextBox)window.FindName(window is MiniPadWindow ? "Editor" : "EditorTextBox");
                    editor.Select(start, length);
                }
            }

            input.Text = "foo";
            if (window is ScratchpadWindow)
                input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var presenter = Field(window, window is NoteEditorWindow ? "_primaryFindPresenter" : "_findPresenter");
            int ScrollVersion() => presenter is null ? 0 : (int)Field(presenter, "_scrollRequestVersion");
            Select(8, 0);
            var scrollVersion = ScrollVersion();
            var richSelection = richEditor.Selection.Start;
            Open();
            Assert.AreEqual(scrollVersion, ScrollVersion(), "Opening must not request result reveal.");
            if (window is ScratchpadWindow)
                Assert.AreEqual(0, richSelection.CompareTo(richEditor.Selection.Start), "Opening must not select/reveal a rich-text result.");
            Assert.AreEqual(1, session.CurrentMatchIndex, window.GetType().Name + " must open from the editor state.");
            Invoke(window, "FindNext");
            Assert.AreEqual(2, session.CurrentMatchIndex);
            Select(3, 3); // With the editor unfocused, refocusing must preserve the query.
            var matches = session.Matches;
            var preferredPosition = session.PreferredNavigationPosition;
            var pendingOwner = Field(window, window is NoteEditorWindow ? "_activeFindPresenter" : "_findPresenter");
            var pendingField = "_hasPendingManualNavigationAnchor";
            Assert.AreEqual(true, Field(pendingOwner, pendingField));
            scrollVersion = ScrollVersion();
            Open();
            Assert.AreEqual(scrollVersion, ScrollVersion(), "Refocusing must not request result reveal.");
            Assert.AreEqual("foo", input.Text);
            Assert.AreSame(matches, session.Matches);
            Assert.AreEqual(2, session.CurrentMatchIndex);
            Assert.AreEqual(preferredPosition, session.PreferredNavigationPosition);
            Assert.AreEqual(true, Field(pendingOwner, pendingField), "Refocusing must preserve pending navigation.");
            Invoke(window, "FindNext");
            Assert.AreEqual(1, session.CurrentMatchIndex, "The preserved manual anchor must affect the next navigation.");
            Close();
            Select(1, 0);
            Open();
            Assert.AreEqual(0, session.CurrentMatchIndex, "Reopening must create a fresh session.");
            Assert.AreEqual(false, Field(pendingOwner, pendingField));
            Close();

            // Seeding a different query must not trigger a reveal or schedule a query refresh.
            Select(4, 2);
            scrollVersion = ScrollVersion();
            var refreshVersion = window is NoteEditorWindow ? Field(window, "_findRefreshVersion") : null;
            Open();
            Assert.AreEqual("xx", input.Text);
            Assert.AreEqual(scrollVersion, ScrollVersion());
            if (window is NoteEditorWindow)
                Assert.AreEqual(refreshVersion, Field(window, "_findRefreshVersion"));
            Close();

            void Refresh() => Invoke(window, "RefreshFindResults", window switch
            {
                MiniPadWindow => [false],
                NoteEditorWindow => [false, null!],
                _ => [false, null!]
            });
            void SetQuery(string query)
            {
                input.Text = query;
                if (window is ScratchpadWindow)
                    input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Refresh(); // NoteEditor normally dispatches this refresh after typing.
            }

            Select(1, 0);
            Open();
            foreach (var forward in new[] { true, false })
            {
                SetQuery("foo");
                Select(9, 0);
                Select(8, 0);
                Assert.AreEqual(true, Field(pendingOwner, pendingField));
                Refresh();
                Assert.AreEqual(true, Field(pendingOwner, pendingField), "An unchanged refresh must preserve manual navigation.");
                SetQuery("fo");
                Assert.AreEqual(1, session.CurrentMatchIndex);
                Assert.AreEqual(false, Field(pendingOwner, pendingField), "A new query must discard the old manual anchor.");
                Invoke(window, forward ? "FindNext" : "FindPrevious");
                Assert.AreEqual(forward ? 2 : 0, session.CurrentMatchIndex,
                    window.GetType().Name + " must navigate from the new query's active result.");

                if (window is NoteEditorWindow)
                {
                    Select(9, 0);
                    Select(8, 0);
                    var matchCase = (CheckBox)window.FindName("MatchCaseOption");
                    matchCase.IsChecked = matchCase.IsChecked != true;
                    Assert.AreEqual(1, session.CurrentMatchIndex);
                    Assert.AreEqual(false, Field(pendingOwner, pendingField), "An option change must discard the old manual anchor.");
                    Invoke(window, forward ? "FindNext" : "FindPrevious");
                    Assert.AreEqual(forward ? 2 : 0, session.CurrentMatchIndex);
                }

                SetQuery("foo");
                Select(10, 0);
                Select(9, 0);
                Assert.AreEqual(true, Field(pendingOwner, pendingField), "Later manual movement must still arm navigation.");
                Invoke(window, forward ? "FindNext" : "FindPrevious");
                Assert.AreEqual(1, session.CurrentMatchIndex);
                Invoke(window, forward ? "FindNext" : "FindPrevious");
                Assert.AreEqual(forward ? 2 : 0, session.CurrentMatchIndex, "The manual anchor must be consumed only once.");
            }
            Close();

            foreach (var selectionLength in new[] { 0, 3 })
            {
                Select(1, 0);
                Open();
                SetQuery("foo");
                Invoke(window, "FindNext");
                Invoke(window, "FindNext");
                Assert.AreEqual(2, session.CurrentMatchIndex);
                Select(3, selectionLength);
                if (window is ScratchpadWindow)
                {
                    var caret = richEditor.CaretPosition;
                    var start = richEditor.Selection.Start;
                    var end = richEditor.Selection.End;
                    Close();
                    Assert.AreEqual(0, caret.CompareTo(richEditor.CaretPosition));
                    Assert.AreEqual(0, start.CompareTo(richEditor.Selection.Start));
                    Assert.AreEqual(0, end.CompareTo(richEditor.Selection.End));
                    Assert.AreEqual(0, ((RichTextBoxSearchPresenter)presenter).GetGeometrySnapshot().Matches.Count);
                }
                else
                {
                    var editor = (TextBox)window.FindName(window is MiniPadWindow ? "Editor" : "EditorTextBox");
                    var caret = editor.CaretIndex;
                    Close();
                    Assert.AreEqual(caret, editor.CaretIndex);
                    Assert.AreEqual(3, editor.SelectionStart);
                    Assert.AreEqual(selectionLength, editor.SelectionLength);
                    Assert.AreEqual(0, ((TextBoxSearchPresenter)presenter!).GetGeometrySnapshot().Matches.Count);
                }
                Assert.IsNull(session.CurrentMatch);
                Assert.AreEqual(0, session.Matches.Count);
                Assert.AreEqual(false, Field(pendingOwner, pendingField));
                Assert.AreEqual(string.Empty, window is ScratchpadWindow
                    ? ((ScratchpadWindowViewModel)window.DataContext).FindResultText
                    : ((TextBlock)window.FindName("FindResultText")).Text);
            }
            // Exercise real keyboard focus on an undisplayed native surface.
            // Do not pump the application dispatcher: this construction test must
            // not start App or its single-instance integration.
            var content = (FrameworkElement)window.Content;
            var localDataContext = content.ReadLocalValue(FrameworkElement.DataContextProperty);
            var inheritedDataContext = content.DataContext;
            window.Content = null;
            content.DataContext = inheritedDataContext;
            using var source = new HwndSource(new HwndSourceParameters("Noted Find seeding regression")
            { Width = 900, Height = 700, WindowStyle = unchecked((int)0x80000000) });
            try
            {
                source.RootVisual = content;
                content.Measure(new Size(900, 700));
                content.Arrange(new Rect(0, 0, 900, 700));
                content.UpdateLayout();
                var editorControl = window is ScratchpadWindow ? (Control)richEditor :
                    (Control)window.FindName(window is MiniPadWindow ? "Editor" : "EditorTextBox");
                Select(1, 0);
                Open();
                SetQuery("foo");
                Invoke(window, "FindNext");
                Invoke(window, "FindNext");
                Assert.IsTrue(editorControl.Focus(), window.GetType().Name + " editor must receive keyboard focus.");
                Select(4, 2);
                Assert.IsTrue(editorControl.IsKeyboardFocusWithin, window.GetType().Name + " editor must have actual keyboard focus.");
                var versionBeforeSeed = ScrollVersion();
                Open();
                Assert.AreEqual("xx", input.Text, "Ctrl+F from the focused editor must seed the selected text.");
                Assert.AreEqual(new TextSearchMatch(4, 2), session.CurrentMatch);
                Assert.AreEqual(false, Field(pendingOwner, pendingField));
                // A changed RichTextBox result cancels old scroll work, but must not actually scroll.
                if (window is not ScratchpadWindow)
                    Assert.AreEqual(versionBeforeSeed, ScrollVersion());
                Invoke(window, "FindNext");
                Assert.AreEqual(1, session.CurrentMatchIndex);

                Assert.IsTrue(editorControl.Focus());
                Select(0, 0);
                var preservedMatches = session.Matches;
                Open();
                Assert.AreEqual("xx", input.Text);
                Assert.AreSame(preservedMatches, session.Matches);
                Assert.AreEqual(1, session.CurrentMatchIndex);
                Assert.AreEqual(true, Field(pendingOwner, pendingField));

                Assert.IsTrue(editorControl.Focus());
                Select(7, 3);
                Assert.IsTrue(input.Focus());
                Open();
                Assert.AreEqual("xx", input.Text, "Ctrl+F in Find must ignore the editor's retained selection.");
                Assert.AreSame(preservedMatches, session.Matches);
                Assert.AreEqual(1, session.CurrentMatchIndex);
                Assert.AreEqual(input.Text.Length, input.SelectionLength);

                Invoke(window, "FindNext");
                Assert.IsTrue(editorControl.Focus());
                Select(11, 2);
                Open();
                Assert.AreEqual("xx", input.Text);
                Assert.AreEqual(new TextSearchMatch(11, 2), session.CurrentMatch,
                    "Seeding the same query must establish the selected occurrence from SelectionStart.");
                Close();
            }
            finally
            {
                source.RootVisual = null;
                window.Content = content;
                if (localDataContext == DependencyProperty.UnsetValue)
                    content.ClearValue(FrameworkElement.DataContextProperty);
                else
                    content.SetValue(FrameworkElement.DataContextProperty, localDataContext);
            }
        }

        static object Field(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        static void Invoke(object target, string name, params object[] args) => target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    }

    private static Button? FindHotkeyEditButton(DependencyObject parent)
    {
        if (parent is Button { Tag: "Notes" } button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var match = FindHotkeyEditButton(VisualTreeHelper.GetChild(parent, index));
            if (match is not null) return match;
        }
        return null;
    }

    private static void VerifySettingsBindings(SettingsView view, AppSettingsService settings, NoteFileService files)
    {
        var originalModel = view.DataContext;
        using var hotkeys = new HotkeyConfiguration(settings, () => new ControlledHotkeyService(), _ => () => { });
        var model = new SettingsViewModel(settings, files, new RejectedStartupService(), hotkeys,
            (_, _) => { }, _ => Assert.Fail("A disabled backup command must not run."),
            () => Array.Empty<DisplayInfo>());
        view.DataContext = model;
        try
        {
            var startup = (CheckBox)view.FindName("RunOnStartupOption");
            startup.SetCurrentValue(CheckBox.IsCheckedProperty, true);
            Assert.IsFalse(model.RunOnStartup);
            Assert.AreEqual(false, startup.IsChecked, "The checkbox must return to the actual startup state.");

            var quick = (RadioButton)view.FindName("NewNoteQuickOption");
            quick.SetCurrentValue(RadioButton.IsCheckedProperty, true);
            Assert.AreEqual(NewNoteMode.Quick, settings.LoadNewNoteMode());
            model.NewNoteMode = NewNoteMode.Both;
            Assert.AreEqual(false, quick.IsChecked);
            Assert.AreEqual(true, ((RadioButton)view.FindName("NewNoteBothOption")).IsChecked);
            quick.SetCurrentValue(RadioButton.IsCheckedProperty, true);
            Assert.AreEqual(NewNoteMode.Quick, model.NewNoteMode);

            var showHeader = (CheckBox)view.FindName("ShowNotesHeaderOption");
            showHeader.SetCurrentValue(CheckBox.IsCheckedProperty, false);
            Assert.IsFalse(settings.LoadShowNotesHeader());

            var accent = (TextBox)view.FindName("AccentHexTextBox");
            accent.SetCurrentValue(TextBox.TextProperty, "#zzzzzz");
            Assert.IsTrue(model.AccentHasError);
            Assert.AreSame(view.FindResource("NotedDangerBrush"), accent.BorderBrush);
            model.NormalizeAccentText();
            Assert.AreEqual(model.ActiveAccent, accent.Text);
            Assert.IsFalse(model.AccentHasError);

            model.UpdateBackupInfo(new(false, false, null, 0, null));
            var restore = (Button)view.FindName("RestoreBackupButton");
            Assert.IsFalse(restore.IsEnabled);
            model.UpdateBackupInfo(new(true, true, DateTime.UtcNow, 1, null));
            Assert.IsTrue(restore.IsEnabled);
            model.CanRestoreBackup = false;
            Assert.IsFalse(restore.IsEnabled);
        }
        finally { view.DataContext = originalModel; }
    }

    private sealed class RejectedStartupService : IRunOnStartupService
    {
        public bool IsRunOnStartupEnabled => false;
        public (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled) =>
            (false, null, "Startup integration is unavailable.");
    }

    private sealed class ConstructionStartupService : IRunOnStartupService
    {
        public bool IsRunOnStartupEnabled => false;
        public (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled) =>
            throw new AssertFailedException("Construction must not change Windows startup integration.");
    }

    private static void VerifyShutdownPreparation(
        NoteEditorWindow editor, ScratchpadWindow scratchpad, TemporaryTestDirectory directory)
    {
        var prepared = typeof(NoteEditorWindow).GetField("_isPreparedForApplicationClose", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Exercise persistence on the laid-out, undisplayed editor.
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        WindowManager.Editor = editor;
        try
        {
            Assert.AreSame(editor, WindowManager.Editor);
            Assert.IsTrue(WindowManager.TryPrepareForShutdown(out _));
            editor.CancelPreparedClose();
            // A hidden Window need not update ActualWidth, so change a persisted
            // preference directly to force a write without showing native UI.
            typeof(NoteEditorWindow).GetField("_scrollSpeed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, 3);

            using (new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(WindowManager.TryPrepareForShutdown(out var error));
                Assert.IsNotNull(error);
                Assert.IsFalse((bool)prepared.GetValue(editor)!);
            }
            Assert.IsTrue(WindowManager.TryPrepareForShutdown(out _), "Editor preparation should be retryable.");
            editor.CancelPreparedClose();

            WindowManager.Scratchpad = scratchpad;
            scratchpad.Width += 80;
            using (new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(WindowManager.TryPrepareForShutdown(out var error));
                Assert.IsNotNull(error);
                Assert.IsFalse((bool)prepared.GetValue(editor)!, "A later failure must undo editor preparation.");
            }
            Assert.IsTrue(WindowManager.TryPrepareForShutdown(out _), "Scratchpad preparation should be retryable.");
        }
        finally
        {
            WindowManager.Editor = null;
            WindowManager.Scratchpad = null;
        }
    }

    private static void VerifyRenderingDuringDocumentChanges()
    {
        // A hidden native surface exercises WPF layout and drawing without opening a visible window.
        using var source = new HwndSource(new HwndSourceParameters("Noted render regression")
        {
            Width = 420, Height = 160, WindowStyle = unchecked((int)0x80000000)
        });
        var textBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
            FontSize = 14, Padding = new Thickness(12), BorderThickness = new Thickness(0),
            Background = Brushes.White, Foreground = Brushes.Black,
            SelectionBrush = Brushes.Teal, SelectionTextBrush = Brushes.White, SelectionOpacity = 1,
            IsInactiveSelectionHighlightEnabled = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var gutter = new LineNumberGutter { Editor = textBox };
        var panel = new DockPanel { Width = 420, Height = 160 };
        DockPanel.SetDock(gutter, Dock.Left);
        panel.Children.Add(gutter);
        panel.Children.Add(textBox);
        source.RootVisual = panel;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        panel.Measure(new Size(420, 160));
        panel.Arrange(new Rect(0, 0, 420, 160));
        Assert.IsTrue(textBox.IsLoaded);

        // This layout request from SelectionChanged is the nested call in the reported crash stack.
        textBox.SelectionChanged += (_, _) => { _ = textBox.LineCount; };
        var longText = string.Join("\n", Enumerable.Repeat("a long document line that wraps when the editor is narrow", 1000));
        for (var i = 0; i < 12; i++)
        {
            textBox.Text = longText;
            panel.UpdateLayout();
            textBox.ScrollToEnd();
            panel.UpdateLayout();
            textBox.Text = i % 2 == 0 ? "one\ntwo\nthree\nfour" : "";
            panel.UpdateLayout();
            Render(panel);
        }

        textBox.Text = "MMMMMMMM";
        textBox.SelectAll();
        panel.UpdateLayout();
        var selectedLetters = Render(textBox);
        textBox.Text = "        ";
        textBox.SelectAll();
        panel.UpdateLayout();
        var selectedSpaces = Render(textBox);
        Assert.IsTrue(selectedLetters.Where((value, index) => value != selectedSpaces[index]).Count() > 30,
            "The selection must leave the selected glyphs visible.");
    }

    private static byte[] Render(FrameworkElement element)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
