using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class UtilityEditingSessionTests
{
    [TestMethod]
    public void TasksRestoreMultipleEditsAndTheFirstInputReplacesTheSavedSelection() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new ChecklistContentService(directory.Path);
        var existingId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var notesText = string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"Line {i:00} of the task notes."));
        content.SaveItems([
            new() { Id = existingId, Text = "existing task", Notes = notesText },
            new() { Id = readingId, Text = "reference" }
        ]);

        Guid newId;
        int start;
        double scroll;
        var original = new ChecklistWindow(settings, content);
        try
        {
            Show(original);
            var model = (ChecklistWindowViewModel)original.DataContext;
            var list = (ListBox)original.FindName("ItemsList");
            KeyboardTestInput.Press(list, Key.N, ModifierKeys.Control);
            Pump();
            var created = model.Items.Last();
            newId = created.Id;
            var title = Field(original, created, "ItemTitleTextBox");
            title.Text = "unfinished title";
            title.Select(2, 4);
            var existing = model.Items.Single(item => item.Id == existingId);
            existing.IsExpanded = true;
            model.Items.Single(item => item.Id == readingId).IsExpanded = true;
            var notes = Field(original, existing, "NotesTextBox");
            Assert.IsTrue(notes.Focus());
            start = notes.Text.IndexOf("Line 15", StringComparison.Ordinal);
            notes.Select(start, 7);
            notes.ScrollToLine(14);
            Pump();
            scroll = notes.VerticalOffset;
            Assert.IsGreaterThan(0, scroll);
            original.PrepareForApplicationShutdown();
        }
        finally { original.Close(); }
        Assert.IsTrue(settings.LoadChecklistWindowState().ReopenOnStartup);
        var notesDirectory = directory.File("notes");
        Directory.CreateDirectory(notesDirectory);
        var backup = new BackupContentCatalog(directory.Path, notesDirectory, directory.File("backup")).Collect();
        Assert.IsFalse(backup.Any(file => file.LogicalPath.Contains("checklist-editing", StringComparison.Ordinal)));

        var reopened = new ChecklistWindow(settings, new ChecklistContentService(directory.Path));
        try
        {
            var model = (ChecklistWindowViewModel)reopened.DataContext;
            var existing = model.Items.Single(item => item.Id == existingId);
            var created = model.Items.Single(item => item.Id == newId);
            Assert.IsTrue(created.IsEditingTitle && model.IsItemEditing(existing));
            Assert.IsTrue(model.Items.Single(item => item.Id == readingId).IsExpanded);
            reopened.ShowWindowWithoutActivation();
            Pump();
            Assert.IsFalse(reopened.IsActive);
            Assert.IsFalse(reopened.IsKeyboardFocusWithin);
            reopened.ShowWindow();
            var notes = Field(reopened, existing, "NotesTextBox");
            Assert.IsTrue(notes.IsKeyboardFocused);
            Assert.AreEqual(start, notes.CaretIndex);
            Assert.AreEqual(start, notes.SelectionStart);
            Assert.AreEqual(7, notes.SelectionLength);
            Pump();
            Assert.AreEqual(scroll, notes.VerticalOffset, 0.5);
            TypeInto(notes, "replacement");
            Assert.AreEqual(notesText.Remove(start, 7).Insert(start, "replacement"), existing.Notes);
            var title = Field(reopened, created, "ItemTitleTextBox");
            Assert.IsTrue(title.Focus());
            Assert.AreEqual(2, title.SelectionStart);
            Assert.AreEqual(4, title.SelectionLength);
            KeyboardTestInput.Press(title, Key.Enter, ModifierKeys.None);
            Pump();
            Assert.IsTrue(model.Items.Last().IsEditingTitle, "Enter in the recovered creation must still create the next task.");
            Assert.HasCount(4, model.Items);
        }
        finally { reopened.Close(); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DictionaryRestoresTheCaretOnTheVerifiedDraftAndKeepsOtherDraftsIndependent(bool reopenPassively) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new DictionaryContentService(directory.Path);
        var existingId = Guid.NewGuid();
        content.SaveItems([new() { Id = existingId, Word = "committed", Description = "accepted" }]);
        Guid addedId;
        var original = new DictionaryWindow(settings, content);
        try
        {
            Show(original);
            var model = (DictionaryWindowViewModel)original.DataContext;
            var existing = model.Items.Single();
            existing.BeginEdit();
            var existingWord = Field(original, existing, "WordTextBox");
            Assert.IsTrue(existingWord.Focus());
            existingWord.Text = "unfinished existing";
            existingWord.Select(1, 5);
            KeyboardTestInput.Press((ItemsControl)original.FindName("ItemsList"), Key.N, ModifierKeys.Control);
            Pump();
            var added = model.Items.Last();
            addedId = added.Id;
            var word = Field(original, added, "WordTextBox");
            word.Text = "abcd";
            word.CaretIndex = 2;
            original.PrepareForApplicationShutdown();
        }
        finally { original.Close(); }
        Assert.IsTrue(settings.LoadDictionaryWindowState().ReopenOnStartup);

        var reopened = new DictionaryWindow(settings, new DictionaryContentService(directory.Path));
        try
        {
            var model = (DictionaryWindowViewModel)reopened.DataContext;
            var added = model.Items.Single(item => item.Id == addedId);
            var existing = model.Items.Single(item => item.Id == existingId);
            Assert.IsTrue(added.IsNew && added.IsEditing && existing.IsEditing);
            if (reopenPassively)
            {
                reopened.ShowWindowWithoutActivation();
                Pump();
                Assert.IsFalse(reopened.IsKeyboardFocusWithin);
            }
            reopened.ShowWindow();
            var word = Field(reopened, added, "WordTextBox");
            Assert.IsTrue(word.IsKeyboardFocused);
            Assert.AreEqual(2, word.CaretIndex);
            Assert.AreEqual(0, word.SelectionLength);
            TypeInto(word, "X");
            Assert.AreEqual("abXcd", added.DraftWord);
            var existingWord = Field(reopened, existing, "WordTextBox");
            Assert.IsTrue(existingWord.Focus());
            Assert.AreEqual(1, existingWord.SelectionStart);
            Assert.AreEqual(5, existingWord.SelectionLength);
            Assert.AreEqual("committed", content.LoadItems().Items.Single().Word);
        }
        finally { reopened.Close(); }
    });

    [TestMethod]
    public void ABlankTaskAcceptedAfterAutosaveCannotReopenAsADeletableCreation() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new ChecklistContentService(directory.Path);
        var sessionPath = directory.File("session", "checklist-editing.json");
        byte[] interrupted;
        Guid id;
        var original = new ChecklistWindow(settings, content);
        try
        {
            Show(original);
            var model = (ChecklistWindowViewModel)original.DataContext;
            KeyboardTestInput.Press((ListBox)original.FindName("ItemsList"), Key.N, ModifierKeys.Control);
            Pump();
            var item = model.Items.Single();
            id = item.Id;
            WaitFor(() => JsonFileStore.Read<List<UtilityItemSession>>(sessionPath).Value?.Single()
                .ChecklistEdit?.Creation is not null);
            Assert.AreEqual(id, content.LoadItems().Items.Single().Id);
            var editor = Field(original, item, "ItemTitleTextBox");
            KeyboardTestInput.Press(editor, Key.Enter, ModifierKeys.Control);
            interrupted = File.ReadAllBytes(sessionPath);
            Assert.IsNull(JsonFileStore.Read<List<UtilityItemSession>>(sessionPath).Value!.Single().ChecklistEdit?.Creation);
        }
        finally { original.Close(); }

        File.WriteAllBytes(sessionPath, interrupted);
        var reopened = new ChecklistWindow(settings, new ChecklistContentService(directory.Path));
        try
        {
            Show(reopened);
            var item = ((ChecklistWindowViewModel)reopened.DataContext).Items.Single();
            Assert.AreEqual(id, item.Id);
            var editor = Field(reopened, item, "ItemTitleTextBox");
            KeyboardTestInput.Press(editor, Key.Escape, ModifierKeys.None);
            Assert.AreEqual(id, content.LoadItems().Items.Single().Id);
        }
        finally { reopened.Close(); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TaskSessionDoesNotReuseTheOldPositionAfterChangesOrDeletion(bool deleted) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new ChecklistContentService(directory.Path);
        var entry = new ChecklistItemState { Id = Guid.NewGuid(), Text = "old text" };
        content.SaveItems([entry]);
        var original = new ChecklistWindow(settings, content);
        try
        {
            Show(original);
            var list = (ListBox)original.FindName("ItemsList");
            var item = ((ChecklistWindowViewModel)original.DataContext).Items.Single();
            KeyboardTestInput.Press((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item), Key.F2, ModifierKeys.None);
            Field(original, item, "ItemTitleTextBox").Select(2, 4);
        }
        finally { original.Close(); }
        content.SaveItems(deleted ? [] : [entry with { Text = "newer accepted text" }]);

        var reopened = new ChecklistWindow(settings, new ChecklistContentService(directory.Path));
        try
        {
            Show(reopened);
            var model = (ChecklistWindowViewModel)reopened.DataContext;
            if (deleted)
            {
                Assert.IsEmpty(model.Items);
                return;
            }
            var item = model.Items.Single();
            var field = Field(reopened, item, "ItemTitleTextBox");
            Assert.AreEqual("newer accepted text", field.Text);
            Assert.IsFalse(field.IsKeyboardFocused);
            Assert.AreEqual(0, field.SelectionLength);
        }
        finally { reopened.Close(); }
    });

    [TestMethod]
    public void OlderDictionarySessionDoesNotApplyItsSelectionToANewerRecoveredDraft() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new DictionaryContentService(directory.Path);
        var original = new DictionaryWindow(settings, content);
        try
        {
            Show(original);
            KeyboardTestInput.Press((ItemsControl)original.FindName("ItemsList"), Key.N, ModifierKeys.Control);
            Pump();
            var item = ((DictionaryWindowViewModel)original.DataContext).Items.Single();
            var word = Field(original, item, "WordTextBox");
            word.Text = "older draft";
            word.Select(2, 4);
        }
        finally { original.Close(); }
        using (var newer = new DictionaryWindowViewModel(content, Dispatcher.CurrentDispatcher, action => action(),
                   false, new DictionaryDraftStore(directory.Path)))
        {
            newer.Items.Single().DraftWord = "newer durable draft";
            Assert.IsTrue(newer.TryFlushPendingItems(out var error), error);
        }
        var reopened = new DictionaryWindow(settings, new DictionaryContentService(directory.Path));
        try
        {
            Show(reopened);
            var item = ((DictionaryWindowViewModel)reopened.DataContext).Items.Single();
            var word = Field(reopened, item, "WordTextBox");
            Assert.AreEqual("newer durable draft", word.Text);
            Assert.IsFalse(word.IsKeyboardFocused);
            Assert.AreEqual(0, word.SelectionLength);
        }
        finally { reopened.Close(); }
    });

    private static void Show(OverlayWindow window)
    {
        window.ShowWindow();
        Pump();
    }

    private static TextBox Field(Window window, object item, string name)
    {
        var list = (ItemsControl)window.FindName("ItemsList");
        window.UpdateLayout();
        return VisualTreeHelpers.FindDescendant<TextBox>(list.ItemContainerGenerator.ContainerFromItem(item), name)!;
    }

    private static void TypeInto(TextBox editor, string text)
    {
        Assert.AreSame(editor, Keyboard.FocusedElement);
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, editor, text))
        {
            RoutedEvent = TextCompositionManager.TextInputEvent
        });
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void WaitFor(Func<bool> check)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        var elapsed = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            if (check() || elapsed.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(check(), "The scheduled session checkpoint did not reach disk.");
    }
}
