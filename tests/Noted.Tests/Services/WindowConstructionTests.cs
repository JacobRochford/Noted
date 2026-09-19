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
                var main = new MainWindow(settings, files, editor, new ConstructionStartupService(),
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
                main.CleanupResources();
                main.Close();
                VerifyShutdownPreparation(app, editor, scratchpad, directory);
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
        App app, NoteEditorWindow editor, ScratchpadWindow scratchpad, TemporaryTestDirectory directory)
    {
        var appEditor = typeof(App).GetField("_noteEditorWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var appScratchpad = typeof(App).GetField("_scratchpadWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var prepared = typeof(NoteEditorWindow).GetField("_isPreparedForApplicationClose", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Exercise persistence on the laid-out, undisplayed editor.
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        appEditor.SetValue(app, editor);
        try
        {
            Assert.IsTrue(app.TryPrepareWindowsForShutdown(out _));
            editor.CancelPreparedClose();
            // A hidden Window need not update ActualWidth, so change a persisted
            // preference directly to force a write without showing native UI.
            typeof(NoteEditorWindow).GetField("_scrollSpeed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, 3);

            using (new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(app.TryPrepareWindowsForShutdown(out var error));
                Assert.IsNotNull(error);
                Assert.IsFalse((bool)prepared.GetValue(editor)!);
            }
            Assert.IsTrue(app.TryPrepareWindowsForShutdown(out _), "Editor preparation should be retryable.");
            editor.CancelPreparedClose();

            appScratchpad.SetValue(app, scratchpad);
            scratchpad.Width += 80;
            using (new FileStream(directory.File("settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsFalse(app.TryPrepareWindowsForShutdown(out var error));
                Assert.IsNotNull(error);
                Assert.IsFalse((bool)prepared.GetValue(editor)!, "A later failure must undo editor preparation.");
            }
            Assert.IsTrue(app.TryPrepareWindowsForShutdown(out _), "Scratchpad preparation should be retryable.");
        }
        finally
        {
            appEditor.SetValue(app, null);
            appScratchpad.SetValue(app, null);
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
