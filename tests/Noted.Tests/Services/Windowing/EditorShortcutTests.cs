using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class EditorShortcutTests
{
    [TestMethod]
    public void MiniPadAndScratchpadRouteLineCommandsAndKeepRichBoldNative() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var miniPad = new MiniPadWindow(new MiniPadRecoveryService(directory.Path), settings);
        var scratchpad = new ScratchpadWindow(new ScratchpadWindowViewModel(settings,
            new ScratchpadContentService(directory.Path)));
        try
        {
            miniPad.Show();
            var plain = (TextBox)miniPad.FindName("Editor");
            AssertWrapping(plain, true);
            AssertWrapping((TextBox)miniPad.FindName("FindTextBox"), false);
            plain.Text = "one\r\ntwo";
            plain.CaretIndex = 1;
            Assert.IsTrue(KeyboardTestInput.Press(plain, Key.Down, ModifierKeys.Alt).Handled);
            Assert.AreEqual("two\r\none", plain.Text);
            scratchpad.Show();
            var rich = (RichTextBox)scratchpad.FindName("Editor");
            rich.Document.Blocks.Clear();
            rich.Document.Blocks.Add(new Paragraph(new Run("test")));
            rich.SelectAll();
            rich.Focus();
            Assert.IsFalse(EditorTextWrapping.HandleKey(rich, Key.B, ModifierKeys.Control));
            EditingCommands.ToggleBold.Execute(null, rich);
            Assert.AreEqual("test\r\n", new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text);
            Assert.AreEqual(FontWeights.Bold, rich.Selection.GetPropertyValue(TextElement.FontWeightProperty));
            AssertWrapping((TextBox)scratchpad.FindName("FindTextBox"), false);
        }
        finally
        {
            miniPad.KeepDraftOnClose();
            miniPad.Close();
            scratchpad.Close();
        }
    });

    [TestMethod]
    public void NoteEditorRoutesBoldToBothPanesAndKeepsTabNavigationAndReopen() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(App).TypeHandle);
        var settings = new AppSettingsService(directory.Path);
        var editor = CreateNoteEditor(directory, settings);
        try
        {
            var firstPath = directory.File("first.md");
            var secondPath = directory.File("second.md");
            File.WriteAllText(firstPath, "first");
            File.WriteAllText(secondPath, "second");
            editor.OpenNote(firstPath);
            editor.OpenNote(secondPath);
            var primary = (TextBox)editor.FindName("EditorTextBox");
            Assert.IsTrue(KeyboardTestInput.Press(primary, Key.Tab, ModifierKeys.Control).Handled);
            Assert.AreEqual(firstPath, editor.Workspace.CurrentDocument!.FilePath);
            Assert.IsTrue(KeyboardTestInput.Press(primary, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift).Handled);
            Assert.AreEqual(secondPath, editor.Workspace.CurrentDocument!.FilePath);

            var second = editor.Workspace.FindByPath(secondPath)!;
            typeof(NoteEditorWindow).GetMethod("ShowSecondaryDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, [second, true, true, true]);
            var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
            var tabs = (MenuItem)editor.FindName("TabsPanelMenuItem");
            Assert.AreEqual("Ctrl+Shift+B", tabs.InputGestureText);
            var initialTabs = tabs.IsChecked;
            foreach (var box in new[] { primary, secondary })
            {
                box.Focus();
                var original = box.Text;
                box.SelectAll();
                Assert.IsTrue(KeyboardTestInput.Press(box, Key.B, ModifierKeys.Control).Handled);
                Assert.AreEqual("**" + original + "**", box.Text);
                Assert.AreEqual(initialTabs, tabs.IsChecked);
                Assert.AreEqual(original, box.SelectedText);
                Assert.IsTrue(KeyboardTestInput.Press(box, Key.B, ModifierKeys.Control).Handled);
                Assert.AreEqual(original, box.Text);
                Assert.IsTrue(KeyboardTestInput.Press(box, Key.B, ModifierKeys.Control | ModifierKeys.Shift).Handled);
                Assert.AreEqual(!initialTabs, tabs.IsChecked);
                Assert.AreEqual(original, box.Text);
                KeyboardTestInput.Press(box, Key.B, ModifierKeys.Control | ModifierKeys.Shift);
                Assert.AreEqual(initialTabs, tabs.IsChecked);
            }

            KeyboardTestInput.Press(secondary, Key.F, ModifierKeys.Control);
            AssertWrapping((TextBox)editor.FindName("FindTextBox"), false);
            AssertWrapping((TextBox)editor.FindName("ReplaceTextBox"), false);
            KeyboardTestInput.Press(secondary, Key.Escape, ModifierKeys.None);
            secondary.Focus();
            Assert.IsTrue(KeyboardTestInput.Press(secondary, Key.W, ModifierKeys.Control).Handled);
            Assert.AreEqual(1, editor.Workspace.ClosedTabCount);
            var reopen = editor.InputBindings.OfType<KeyBinding>().Single(binding =>
                ReferenceEquals(binding.Command, NoteEditorWindow.ReopenClosedTabCommand));
            Assert.AreEqual(Key.T, reopen.Key);
            Assert.AreEqual(ModifierKeys.Control | ModifierKeys.Shift, reopen.Modifiers);
            Assert.IsTrue(NoteEditorWindow.ReopenClosedTabCommand.CanExecute(null, editor));
            NoteEditorWindow.ReopenClosedTabCommand.Execute(null, editor);
            Assert.AreEqual(2, editor.Workspace.Documents.Count);
            Assert.AreEqual(0, editor.Workspace.ClosedTabCount);
        }
        finally { CloseNoteEditor(editor); }
    });

    [TestMethod]
    public void ChecklistFormatsNotesAndCtrlFFocusesItsExistingSearch() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ChecklistContentService(directory.Path);
        service.SaveItems([new ChecklistItemState { Text = "title", Notes = "notes" }]);
        var window = new ChecklistWindow(new AppSettingsService(directory.Path), service);
        try
        {
            var model = (ChecklistWindowViewModel)window.DataContext;
            model.Items[0].IsExpanded = true;
            window.Show();
            Layout(window);
            var list = (ListBox)window.FindName("ItemsList");
            var row = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(model.Items[0]);
            var notes = VisualTreeHelpers.FindDescendant<TextBox>(row, "NotesTextBox")!;
            var title = VisualTreeHelpers.FindDescendant<TextBox>(row, "ItemTitleTextBox")!;
            Assert.IsNotNull(notes);
            AssertWrapping(notes, true);
            AssertWrapping(title, false);
            AssertWrapping((TextBox)window.FindName("SearchBox"), false);
            AssertWrapping((TextBox)window.FindName("TabNameTextBox"), false);

            model.IsSearchVisible = false;
            Assert.IsTrue(KeyboardTestInput.Press(notes, Key.F, ModifierKeys.Control).Handled);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(model.IsSearchVisible);
            Assert.IsTrue(((TextBox)window.FindName("SearchBox")).IsKeyboardFocused);
            Assert.IsTrue(KeyboardTestInput.Press(notes, Key.F, ModifierKeys.Control).Handled);
            Assert.IsTrue(model.IsSearchVisible, "Ctrl+F must not toggle the search closed.");
            Assert.IsFalse(window.HandleSearchShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
            notes.Text = "persist **notes**";
            Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
            Assert.AreEqual("persist **notes**", service.LoadItems().Items.Single().Notes);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void QuickJotJotsAndDictionaryKeepFormattingOutOfNamingFields() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        using var workspace = new JotsWorkspace(new JotsContentService(directory.Path), Dispatcher.CurrentDispatcher);
        var capture = new JotCaptureWindow(new JotCaptureViewModel(workspace,
            new JotCaptureDraftService(directory.Path), Dispatcher.CurrentDispatcher));
        var jots = new JotsWindow(settings, workspace);
        var dictionaryService = new DictionaryContentService(directory.Path);
        dictionaryService.SaveItems([new DictionaryItemState { Word = "word", Description = "definition" }]);
        var dictionary = new DictionaryWindow(settings, dictionaryService);
        try
        {
            AssertWrapping((TextBox)capture.FindName("CaptureTextBox"), true);
            foreach (var modifiers in new[] { ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Alt })
            {
                Assert.IsFalse(jots.HandleShortcut(Key.N, modifiers));
                Assert.IsFalse(jots.HandleShortcut(Key.F, modifiers));
            }
            Assert.IsTrue(jots.HandleShortcut(Key.N, ModifierKeys.Control));
            var model = (JotsWindowViewModel)jots.DataContext;
            Assert.IsNotNull(model.SelectedItem);
            AssertWrapping((TextBox)jots.FindName("DetailsTextBox"), true);
            AssertWrapping((TextBox)jots.FindName("TitleTextBox"), false);
            AssertWrapping((TextBox)jots.FindName("SearchTextBox"), false);
            model.SelectedItem.DraftDetails = "save this";
            foreach (var modifiers in new[] { ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Alt })
            {
                Assert.IsFalse(jots.HandleShortcut(Key.S, modifiers));
                Assert.IsFalse(jots.HandleShortcut(Key.Enter, modifiers));
                Assert.IsTrue(model.SelectedItem.IsEditing);
            }
            Assert.IsTrue(jots.HandleShortcut(Key.S, ModifierKeys.Control));
            Assert.IsFalse(model.SelectedItem.IsEditing);

            var dictionaryModel = (DictionaryWindowViewModel)dictionary.DataContext;
            dictionaryModel.Items[0].BeginEdit();
            dictionary.Show();
            Layout(dictionary);
            var list = (ItemsControl)dictionary.FindName("ItemsList");
            var row = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(dictionaryModel.Items[0]);
            AssertWrapping(VisualTreeHelpers.FindDescendant<TextBox>(row, "DefinitionBox")!, true);
            AssertWrapping(VisualTreeHelpers.FindDescendant<TextBox>(row, "WordTextBox")!, false);
            AssertWrapping((TextBox)dictionary.FindName("SearchTextBox"), false);
            dictionaryModel.Items[0].CancelEdit();
        }
        finally
        {
            ((JotsWindowViewModel)jots.DataContext).CancelSelectedEdit();
            foreach (var item in ((DictionaryWindowViewModel)dictionary.DataContext).Items)
                item.CancelEdit();
            capture.PrepareForShutdown();
            capture.Close();
            jots.Close();
            dictionary.Close();
        }
    });

    [TestMethod]
    public void CopyNameAcceptsNoteListFocusAndLeavesTextInputsToNativeCopy() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        var editor = CreateNoteEditor(directory, settings);
        var backup = new BackupCoordinator(new FullBackupService(directory.Path, files.NotesDirectory),
            new ShutdownFlushCoordinator(), () => files.NotesDirectory);
        var main = new MainWindow(settings, files, editor, new NoteOperations(files, editor.Workspace),
            new TestStartupService(), backup, (_, _) => { });
        try
        {
            var list = (ListBox)main.FindName("FileList");
            list.ItemsSource = new[] { new NoteItem { DisplayName = "copy this name" } };
            list.SelectedIndex = 0;
            string? copied = null;
            Assert.IsTrue(main.HandleCopyNameShortcut(Key.C, ModifierKeys.Control, list, text => copied = text));
            Assert.AreEqual("copy this name", copied);
            foreach (var source in new[] { (TextBox)main.FindName("SearchBox"), new TextBox { Text = "rename" } })
            {
                source.SelectAll();
                Assert.IsFalse(main.HandleCopyNameShortcut(Key.C, ModifierKeys.Control, source,
                    _ => Assert.Fail("Text copying must not copy a note name.")));
            }
            Assert.IsFalse(main.HandleCopyNameShortcut(Key.C, ModifierKeys.Control | ModifierKeys.Shift, list));
        }
        finally
        {
            main.CleanupResources();
            main.Close();
            CloseNoteEditor(editor);
        }
    });

    private static void AssertWrapping(TextBox editor, bool enabled)
    {
        Assert.IsNotNull(editor);
        foreach (var (key, modifiers, expected) in new[]
        {
            (Key.B, ModifierKeys.Control, "**test**"), (Key.I, ModifierKeys.Control, "*test*"),
            (Key.K, ModifierKeys.Control, "[test](url)"), (Key.E, ModifierKeys.Control | ModifierKeys.Shift, "`test`")
        })
        {
            editor.Text = "test";
            editor.SelectAll();
            KeyboardTestInput.Press(editor, key, modifiers);
            Assert.AreEqual(enabled ? expected : "test", editor.Text, $"{editor.Name}: {key}");
        }
        editor.Text = "test";
    }

    private static NoteEditorWindow CreateNoteEditor(TemporaryTestDirectory directory, AppSettingsService settings) =>
        new(new NoteContentService(directory.Path), settings, new NoteRecoveryService(directory.Path),
            new NoteEditorSessionService(directory.Path));

    private static void CloseNoteEditor(NoteEditorWindow editor)
    {
        foreach (var document in editor.Workspace.Documents)
            editor.Workspace.MarkSaved(document);
        editor.TryPrepareForClose();
        editor.Close();
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
    }

    private sealed class TestStartupService : IRunOnStartupService
    {
        public bool IsRunOnStartupEnabled => false;
        public (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled) =>
            throw new AssertFailedException("Shortcut tests must not change Windows startup settings.");
    }
}
