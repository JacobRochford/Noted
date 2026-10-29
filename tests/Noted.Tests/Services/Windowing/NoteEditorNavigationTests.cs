using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEditorNavigationTests
{
    [TestMethod]
    public void OpenShortcutUsesSupportedFileDialogAndReusesDirtyOpenTab() => WithEditor((editor, directory, _, files) =>
    {
        var path = CreateNote(files, "existing.md", "saved");
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control, chooseFile: dialog =>
        {
            dialog.FileName = path;
            return true;
        }));
        var document = editor.Workspace.CurrentDocument!;
        var box = (TextBox)editor.FindName("EditorTextBox");
        box.Text = "unsaved edits";
        box.Select(2, 4);
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control, chooseFile: dialog =>
        {
            Assert.AreEqual(files.CurrentDirectory, dialog.InitialDirectory);
            Assert.AreEqual(NoteFileExtensions.FileDialogFilter, dialog.Filter);
            Assert.IsTrue(dialog.CheckFileExists);
            Assert.IsFalse(dialog.Multiselect);
            dialog.FileName = path;
            return true;
        }));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreSame(document, editor.Workspace.CurrentDocument);
        Assert.AreEqual("unsaved edits", box.Text);
        Assert.AreEqual(2, box.SelectionStart);
        Assert.AreEqual(4, box.SelectionLength);
        Assert.IsTrue(document.IsDirty);
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control, chooseFile: _ => false));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreEqual("saved", File.ReadAllText(path));
    });

    [TestMethod]
    public void RevealShortcutUsesCurrentPaneAndKeepsMissingFilesSafe() => WithEditor((editor, _, _, files) =>
    {
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift,
            reveal: _ => Assert.Fail("No note is active.")));
        var first = CreateNote(files, "first.md", "first");
        var second = CreateNote(files, "second.md", "second");
        editor.OpenNote(first);
        editor.OpenNote(second);
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var current = editor.Workspace.CurrentDocument!;
        ProcessStartInfo? start = null;
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift, reveal: info => start = info));
        Assert.IsNotNull(start);
        Assert.AreEqual("explorer.exe", start.FileName);
        Assert.AreEqual($"/select,\"{current.FilePath}\"", start.Arguments);
        Assert.IsTrue(start.UseShellExecute);
        File.Delete(current.FilePath);
        editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift, reveal: info => start = info);
        Assert.AreEqual($"\"{files.NotesDirectory}\"", start.Arguments);
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Alt));
    });

    [TestMethod]
    public void QuickOpenUsesManagedFilenamesAndExistingOpenPathWithoutDuplicatingTabs() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "one/same.md", "first");
        var second = CreateNote(files, "two/same.md", "second");
        CreateNote(files, "not-indexed.bin", "ignore");
        CreateNote(files, "archived.md", "archived");
        Assert.IsTrue(files.ArchiveNote("archived.md").Success);
        editor.OpenNote(first);
        var original = editor.Workspace.CurrentDocument;
        var box = (TextBox)editor.FindName("EditorTextBox");
        box.Text = "dirty";
        var snapshot = editor.GetQuickOpenNotes();
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual(2, snapshot.Count(note => note.FileName == "same.md"));
        Assert.AreNotEqual(snapshot[0].DisplayPath, snapshot[1].DisplayPath);
        Assert.IsTrue(editor.HandleNavigationShortcut(Key.P, ModifierKeys.Control, chooseNote: dialog =>
        {
            var results = (ListBox)dialog.FindName("ResultsList");
            results.SelectedItem = results.Items.OfType<QuickOpenNote>().Single(note => note.FullPath == first);
            Assert.IsTrue(dialog.TrySelectCurrent());
            dialog.Close();
            return true;
        }));
        Assert.AreEqual(1, editor.Workspace.Documents.Count);
        Assert.AreSame(original, editor.Workspace.CurrentDocument);
        Assert.AreEqual("dirty", box.Text);
        Assert.IsTrue(editor.OpenQuickNote(dialog =>
        {
            var results = (ListBox)dialog.FindName("ResultsList");
            results.SelectedItem = results.Items.OfType<QuickOpenNote>().Single(note => note.FullPath == second);
            Assert.IsTrue(dialog.TrySelectCurrent());
            dialog.Close();
            return true;
        }));
        Assert.AreEqual(2, editor.Workspace.Documents.Count);
        Assert.AreEqual(second, editor.OpenFilePath);
    });

    [TestMethod]
    public void QuickOpenFiltersInMemoryNavigatesAndCancelsWithoutOpening() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "a/alpha.md", "alpha");
        var second = CreateNote(files, "b/alpha.md", "alpha");
        CreateNote(files, "beta.txt", "alpha content must not match");
        var dialog = new QuickOpenDialog(editor.GetQuickOpenNotes()) { Owner = editor };
        CreateNote(files, "new-alpha.md", "added after the snapshot");
        Assert.IsFalse(ShowWithInput(dialog, () =>
        {
            var query = (TextBox)dialog.FindName("QueryTextBox");
            var results = (ListBox)dialog.FindName("ResultsList");
            query.Text = "ALPHA";
            Assert.AreEqual(2, results.Items.Count);
            Assert.AreEqual(first, ((QuickOpenNote)results.SelectedItem).FullPath);
            KeyboardTestInput.Press(query, Key.Down, ModifierKeys.None);
            Assert.AreEqual(second, ((QuickOpenNote)results.SelectedItem).FullPath);
            KeyboardTestInput.Press(query, Key.Up, ModifierKeys.None);
            Assert.AreEqual(first, ((QuickOpenNote)results.SelectedItem).FullPath);
            query.Text = "no results";
            Assert.AreEqual(0, results.Items.Count);
            Assert.IsFalse(dialog.TrySelectCurrent());
            Assert.AreEqual(Visibility.Visible, ((TextBlock)dialog.FindName("EmptyText")).Visibility);
            KeyboardTestInput.Press(query, Key.Escape, ModifierKeys.None);
        }));
        Assert.IsNull(dialog.SelectedFilePath);
        Assert.AreEqual(0, editor.Workspace.Documents.Count);
        Assert.IsTrue(editor.GetQuickOpenNotes().Any(note => note.FileName == "new-alpha.md"));
    });

    [TestMethod]
    public void QuickOpenRejectsStaleEntriesAndEnterSelectsTheRemainingNote() => WithEditor((editor, _, _, files) =>
    {
        var stale = CreateNote(files, "old.md", "old");
        var valid = CreateNote(files, "valid.md", "valid");
        var dialog = new QuickOpenDialog(editor.GetQuickOpenNotes()) { Owner = editor };
        File.Move(stale, Path.Combine(files.NotesDirectory, "renamed.md"));
        Assert.IsTrue(ShowWithInput(dialog, () =>
        {
            var query = (TextBox)dialog.FindName("QueryTextBox");
            Assert.IsFalse(dialog.TrySelectCurrent());
            Assert.AreEqual(1, ((ListBox)dialog.FindName("ResultsList")).Items.Count);
            Assert.IsFalse(string.IsNullOrEmpty(((TextBlock)dialog.FindName("ErrorText")).Text));
            KeyboardTestInput.Press(query, Key.Enter, ModifierKeys.None);
        }));
        Assert.AreEqual(valid, dialog.SelectedFilePath);
        Assert.IsTrue(editor.GetQuickOpenNotes().Any(note => note.FileName == "renamed.md"));
    });

    [TestMethod]
    public void SplitToggleAndPaneFocusKeepDirtyContentAssignmentsAndSelections() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.md", "first"));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        Assert.IsTrue(KeyboardTestInput.Press(primary, Key.D2, ModifierKeys.Control).Handled);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        primary.Text = "dirty primary";
        primary.Select(1, 3);
        var primaryDocument = editor.Workspace.ActiveDocument!;
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        var secondaryDocument = editor.Workspace.SecondaryDocument!;
        Assert.IsNotNull(secondaryDocument);
        secondary.Text = "dirty secondary";
        secondary.Select(2, 4);
        KeyboardTestInput.Press(secondary, Key.D1, ModifierKeys.Control);
        Assert.IsTrue(primary.IsKeyboardFocused);
        Assert.AreSame(primaryDocument, editor.Workspace.CurrentDocument);
        Assert.AreSame(primaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.AreEqual(1, primary.SelectionStart);
        Assert.AreEqual(3, primary.SelectionLength);
        KeyboardTestInput.Press(primary, Key.D2, ModifierKeys.Control);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        Assert.AreSame(secondaryDocument, editor.Workspace.CurrentDocument);
        Assert.AreSame(secondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.AreEqual(2, secondary.SelectionStart);
        Assert.AreEqual(4, secondary.SelectionLength);
        KeyboardTestInput.Press(secondary, Key.Oem5, ModifierKeys.Control);
        Assert.IsNull(editor.Workspace.SecondaryDocument);
        Assert.IsTrue(primary.IsKeyboardFocused);
        Assert.AreEqual("dirty secondary", secondaryDocument.Content);
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        Assert.AreSame(secondaryDocument, editor.Workspace.SecondaryDocument);
        Assert.AreSame(primaryDocument, editor.Workspace.ActiveDocument);
        Assert.AreEqual("dirty primary", primary.Text);
        Assert.AreEqual("dirty secondary", secondary.Text);
        Assert.IsTrue(primaryDocument.IsDirty);
        Assert.IsTrue(secondaryDocument.IsDirty);
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.D1, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.D2, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.IsFalse(editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control | ModifierKeys.Shift));
    });

    [TestMethod]
    public void SwitchingSplitFocusPreservesUndoAndHighlightsTheCurrentTab() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.md", "first"));
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var tabs = (ListBox)editor.FindName("OpenTabsList");
        secondary.Select(secondary.Text.Length, 0);
        secondary.SelectedText = " edited";

        KeyboardTestInput.Press(secondary, Key.D1, ModifierKeys.Control);
        Assert.AreSame(editor.Workspace.ActiveDocument, tabs.SelectedItem);
        KeyboardTestInput.Press(primary, Key.D2, ModifierKeys.Control);
        Assert.AreSame(editor.Workspace.SecondaryDocument, tabs.SelectedItem);
        Assert.IsTrue(secondary.CanUndo);
        secondary.Undo();
        Assert.AreEqual("first", secondary.Text);
        Assert.AreEqual("second", primary.Text);
    });

    [TestMethod]
    public void NewNoteShortcutKeepsTypingInTheFocusedSplitPane() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "first.md", "first"));
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var primary = editor.Workspace.ActiveDocument;
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        editor.NewNoteRequested += (_, _) => editor.OpenNote(CreateNote(files, "new.md", ""));

        KeyboardTestInput.Press(secondary, Key.N, ModifierKeys.Control);

        Assert.AreSame(primary, editor.Workspace.ActiveDocument);
        Assert.AreEqual("new.md", Path.GetFileName(editor.Workspace.SecondaryDocument!.FilePath));
        Assert.AreSame(editor.Workspace.SecondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        secondary.SelectedText = "typed into new note";
        Assert.AreEqual("typed into new note", editor.Workspace.SecondaryDocument.Content);
        Assert.AreEqual("second", ((TextBox)editor.FindName("EditorTextBox")).Text);
    });

    [TestMethod]
    public void ReopeningAnAlreadyVisibleSecondaryPreviewFocusesThePreview() => WithEditor((editor, _, _, files) =>
    {
        var first = CreateNote(files, "first.md", "# first");
        editor.OpenNote(first);
        editor.OpenNote(CreateNote(files, "second.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        KeyboardTestInput.Press(secondary, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
        editor.HandleNavigationShortcut(Key.D1, ModifierKeys.Control);

        Assert.IsTrue(editor.OpenNote(first));

        Assert.IsTrue(((FrameworkElement)editor.FindName("SecondaryMarkdownPreview")).IsKeyboardFocusWithin);
        Assert.AreSame(editor.Workspace.SecondaryDocument, ((ListBox)editor.FindName("OpenTabsList")).SelectedItem);
    });

    [TestMethod]
    public void DraggingAnotherTabIntoSplitKeepsThePreviouslyVisibleNote() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) editor.OpenNote(CreateNote(files, name, name));
        var previous = editor.Workspace.ActiveDocument;
        var dragged = editor.Workspace.Documents[1];
        var tabs = (ListBox)editor.FindName("OpenTabsList");
        editor.UpdateLayout();
        var container = (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromItem(dragged);
        Assert.IsNotNull(container);
        var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = container
        };
        typeof(NoteEditorWindow).GetMethod("OpenTabsList_PreviewMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [tabs, mouseDown]);
        tabs.SelectedItem = dragged;
        Assert.AreSame(dragged, editor.Workspace.ActiveDocument);

        typeof(NoteEditorWindow).GetMethod("ShowSecondaryDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [dragged, true, true, true]);

        Assert.AreSame(previous, editor.Workspace.ActiveDocument);
        Assert.AreSame(dragged, editor.Workspace.SecondaryDocument);
        Assert.AreEqual("c.md", ((TextBox)editor.FindName("EditorTextBox")).Text);
        Assert.AreEqual("b.md", ((TextBox)editor.FindName("SecondaryEditorTextBox")).Text);
    });

    [TestMethod]
    public void MovingPrimaryToTheRightKeepsTheExistingOtherPaneAndItsEdits() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) editor.OpenNote(CreateNote(files, name, name));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var previousPrimary = editor.Workspace.ActiveDocument;
        var previousSecondary = editor.Workspace.SecondaryDocument;
        ((TextBox)editor.FindName("SecondaryEditorTextBox")).Text = "unsaved secondary";

        typeof(NoteEditorWindow).GetMethod("ShowSecondaryDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [previousPrimary, true, true, true]);

        Assert.AreSame(previousSecondary, editor.Workspace.ActiveDocument);
        Assert.AreSame(previousPrimary, editor.Workspace.SecondaryDocument);
        Assert.AreEqual("unsaved secondary", ((TextBox)editor.FindName("EditorTextBox")).Text);
        Assert.AreEqual("c.md", ((TextBox)editor.FindName("SecondaryEditorTextBox")).Text);
    });

    [TestMethod]
    public void CyclingFromSecondaryUsesCurrentTabAndChangesOnlyTheIntendedPane() => WithEditor((editor, _, _, files) =>
    {
        foreach (var name in new[] { "a.md", "b.md", "c.md" }) editor.OpenNote(CreateNote(files, name, name));
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        KeyboardTestInput.Press(primary, Key.Oem5, ModifierKeys.Control);
        var primaryDocument = editor.Workspace.ActiveDocument;
        Assert.AreEqual("a.md", Path.GetFileName(editor.OpenFilePath));
        secondary.Text = "keep this unsaved";
        var originalSecondary = editor.Workspace.SecondaryDocument!;
        KeyboardTestInput.Press(secondary, Key.Tab, ModifierKeys.Control);
        Assert.AreEqual("b.md", Path.GetFileName(editor.OpenFilePath));
        Assert.AreSame(primaryDocument, editor.Workspace.ActiveDocument);
        Assert.AreEqual("keep this unsaved", originalSecondary.Content);
        KeyboardTestInput.Press(secondary, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.AreSame(originalSecondary, editor.Workspace.SecondaryDocument);
        Assert.AreEqual("keep this unsaved", secondary.Text);
    });

    [TestMethod]
    public void NativeEditMenuCommandsTargetFocusedPaneAndPreserveUndo() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.md", "first"));
        editor.OpenNote(CreateNote(files, "b.md", "second"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var edit = (MenuItem)editor.FindName("EditMenu");
        foreach (var box in new[] { primary, secondary })
        {
            Assert.IsTrue(box.Focus(), $"Could not focus {box.Name}; current focus: {Keyboard.FocusedElement}");
            box.Select(1, 0);
            box.SelectedText = "added";
            var changed = box.Text;
            edit.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, edit));
            var native = edit.Items.OfType<MenuItem>().Where(item => item.Command is RoutedCommand command &&
                command.OwnerType == typeof(ApplicationCommands)).ToList();
            Assert.AreEqual(6, native.Count);
            foreach (var item in native) Assert.AreSame(box, item.CommandTarget,
                $"Expected {box.Name}; focused: {((FrameworkElement?)Keyboard.FocusedElement)?.Name}; current: {editor.OpenFilePath}");
            ApplicationCommands.Undo.Execute(null, box);
            Assert.AreNotEqual(changed, box.Text);
            ApplicationCommands.Redo.Execute(null, box);
            Assert.AreEqual(changed, box.Text);
            ApplicationCommands.SelectAll.Execute(null, box);
            Assert.AreEqual(box.Text, box.SelectedText);
        }
    });

    [TestMethod]
    public void GoToLineTargetsTheFocusedPaneAndValidatesDialogInput() => WithEditor((editor, _, _, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.md", "a\r\nb\r\nc"));
        editor.OpenNote(CreateNote(files, "b.md", "one\ntwo"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var primaryCaret = primary.CaretIndex;
        Exception? error = null;
        editor.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<GoToLineDialog>().Single();
            try
            {
                var input = (TextBox)dialog.FindName("LineNumberTextBox");
                input.Text = "0";
                KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None);
                Assert.IsTrue(dialog.IsVisible);
                Assert.IsFalse(string.IsNullOrEmpty(((TextBlock)dialog.FindName("ErrorText")).Text));
                input.Text = "3";
                KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None);
            }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        Assert.IsTrue(KeyboardTestInput.Press(secondary, Key.G, ModifierKeys.Control).Handled);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        Assert.AreEqual(6, secondary.CaretIndex);
        Assert.IsTrue(secondary.IsKeyboardFocused);
        Assert.AreEqual(primaryCaret, primary.CaretIndex);
        Assert.AreEqual("a\r\nb\r\nc", secondary.Text);
    });

    [TestMethod]
    public void WordWrapMatchesExistingControlsAndDoesNotRunInSearchInputs() => WithEditor((editor, directory, settings, files) =>
    {
        editor.OpenNote(CreateNote(files, "a.md", "a"));
        editor.OpenNote(CreateNote(files, "b.md", "b"));
        editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
        var secondary = (TextBox)editor.FindName("SecondaryEditorTextBox");
        var primary = (TextBox)editor.FindName("EditorTextBox");
        var before = primary.TextWrapping;
        KeyboardTestInput.Press(secondary, Key.Z, ModifierKeys.Alt);
        Assert.AreNotEqual(before, primary.TextWrapping);
        Assert.AreEqual(primary.TextWrapping, secondary.TextWrapping);
        Assert.AreEqual(primary.TextWrapping == TextWrapping.Wrap, ((MenuItem)editor.FindName("HeaderWordWrapMenuItem")).IsChecked);
        var search = (TextBox)editor.FindName("FindTextBox");
        var after = primary.TextWrapping;
        KeyboardTestInput.Press(search, Key.Z, ModifierKeys.Alt);
        Assert.AreEqual(after, primary.TextWrapping);

        var miniPad = new MiniPadWindow(new MiniPadRecoveryService(directory.Path), settings);
        var viewModel = new ScratchpadWindowViewModel(settings, new ScratchpadContentService(directory.Path));
        var scratchpad = new ScratchpadWindow(viewModel);
        try
        {
            miniPad.Show();
            var miniEditor = (TextBox)miniPad.FindName("Editor");
            var miniBefore = miniEditor.TextWrapping;
            KeyboardTestInput.Press(miniEditor, Key.Z, ModifierKeys.Alt);
            Assert.AreNotEqual(miniBefore, miniEditor.TextWrapping);
            Assert.AreEqual(miniEditor.TextWrapping == TextWrapping.Wrap,
                settings.LoadMiniPadWindows()[miniPad.InstanceId].WordWrapEnabled);
            scratchpad.Show();
            var rich = (RichTextBox)scratchpad.FindName("Editor");
            var richBefore = viewModel.WordWrapEnabled;
            KeyboardTestInput.Press(rich, Key.Z, ModifierKeys.Alt);
            Assert.AreEqual(!richBefore, viewModel.WordWrapEnabled);
            Assert.AreEqual(viewModel.WordWrapEnabled, settings.LoadScratchpadWindowState().WordWrapEnabled);
            Assert.AreEqual(viewModel.WordWrapEnabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                rich.HorizontalScrollBarVisibility);
            var richFind = (TextBox)scratchpad.FindName("FindTextBox");
            KeyboardTestInput.Press(richFind, Key.Z, ModifierKeys.Alt);
            Assert.AreEqual(!richBefore, viewModel.WordWrapEnabled);
        }
        finally
        {
            miniPad.KeepDraftOnClose();
            miniPad.Close();
            scratchpad.Close();
        }
    });

    private static bool ShowWithInput(Window dialog, Action input)
    {
        Exception? error = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
        {
            try { input(); }
            catch (Exception ex) { error = ex; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        var accepted = dialog.ShowDialog();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return accepted == true;
    }

    private static string CreateNote(NoteFileService files, string name, string content)
    {
        var path = Path.GetFullPath(Path.Combine(files.NotesDirectory, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WithEditor(Action<NoteEditorWindow, TemporaryTestDirectory, AppSettingsService, NoteFileService> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveNotesDirectory(directory.File("notes"));
        using var files = new NoteFileService(settings);
        var editor = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
            new NoteRecoveryService(directory.Path), new NoteEditorSessionService(directory.Path), files);
        try
        {
            editor.Show();
            action(editor, directory, settings, files);
        }
        finally
        {
            foreach (var document in editor.Workspace.Documents) editor.Workspace.MarkSaved(document);
            editor.TryPrepareForClose();
            editor.Close();
        }
    });
}
