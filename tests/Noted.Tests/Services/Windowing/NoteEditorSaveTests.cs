using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEditorSaveTests
{
    [TestMethod]
    [DataRow(".md")]
    [DataRow(".json")]
    [DataRow(".CS")]
    public void FirstSaveUpdatesTheDocumentAndSessionWithoutResettingEditingState(string extension) =>
        WithNewNote((editor, source, recovery, session, directory) =>
    {
        var destination = directory.File("chosen" + extension);
        var document = editor.Workspace.CurrentDocument!;
        var box = (TextBox)editor.FindName("EditorTextBox");
        Assert.IsTrue(editor.TrySaveCurrentNote(dialog =>
        {
            Assert.IsFalse(dialog.OverwritePrompt);
            dialog.FileName = destination;
            return true;
        }, _ => throw new AssertFailedException("A new path needs no replacement prompt.")));

        Assert.AreSame(document, editor.Workspace.CurrentDocument);
        Assert.AreEqual(destination, document.FilePath);
        Assert.AreEqual("editor edits", File.ReadAllText(destination));
        Assert.IsFalse(File.Exists(source));
        Assert.IsFalse(document.IsDirty);
        Assert.IsFalse(document.UsesGeneratedName);
        Assert.IsNull(document.InitialFileContent);
        Assert.IsNull(recovery.LoadDraft(source).Draft);
        Assert.AreEqual(destination, session.Load().Session.ActiveFilePath);
        Assert.AreEqual(destination, session.Load().Session.Tabs.Single().FilePath);
        Assert.AreEqual(2, box.SelectionStart);
        Assert.AreEqual(4, box.SelectionLength);
        box.Undo();
        Assert.AreEqual("", box.Text);
        box.Redo();
        Assert.AreEqual("editor edits", box.Text);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FirstSaveHonorsTheReplacementDecision(bool replace) =>
        WithNewNote((editor, source, recovery, session, directory) =>
    {
        var destination = directory.File("existing.md");
        File.WriteAllText(destination, "existing content");
        var prompts = 0;

        var saved = editor.TrySaveCurrentNote(dialog => { dialog.FileName = destination; return true; }, path =>
        {
            Assert.AreEqual(destination, path);
            prompts++;
            return replace;
        });

        Assert.AreEqual(replace, saved);
        Assert.AreEqual(1, prompts);
        Assert.AreEqual(replace ? "editor edits" : "existing content", File.ReadAllText(destination));
        if (!replace)
            AssertOriginalDraft(editor, source, recovery, session);
    });

    [TestMethod]
    public void CancelingFirstSaveKeepsTheOriginalDraft() =>
        WithNewNote((editor, source, recovery, session, _) =>
    {
        Assert.IsFalse(editor.TrySaveCurrentNote(_ => false));
        AssertOriginalDraft(editor, source, recovery, session);
    });

    [TestMethod]
    public void FirstSaveCanKeepTheOriginalFilenameAfterConfirmation() =>
        WithNewNote((editor, source, recovery, session, _) =>
    {
        Assert.IsTrue(editor.TrySaveCurrentNote(dialog => { dialog.FileName = source; return true; }, _ => true));
        Assert.AreEqual(source, editor.Workspace.CurrentDocument!.FilePath);
        Assert.IsFalse(editor.Workspace.CurrentDocument.IsDirty);
        Assert.IsFalse(editor.Workspace.CurrentDocument.UsesGeneratedName);
        Assert.AreEqual("editor edits", File.ReadAllText(source));
        Assert.IsNull(recovery.LoadDraft(source).Draft);
        Assert.AreEqual(source, session.Load().Session.ActiveFilePath);
    });

    [TestMethod]
    public void NamedNewNoteSavesWithoutOpeningAnotherNamingDialog() =>
        WithNewNote((editor, source, _, _, _) =>
    {
        editor.Workspace.CurrentDocument!.UsesGeneratedName = false;
        Assert.IsTrue(editor.TrySaveCurrentNote(_ => throw new AssertFailedException("A named note saves in place.")));
        Assert.AreEqual(source, editor.Workspace.CurrentDocument.FilePath);
        Assert.AreEqual("editor edits", File.ReadAllText(source));
        Assert.IsFalse(editor.Workspace.CurrentDocument.IsDirty);
    });

    [TestMethod]
    public void FailedFirstSaveKeepsOriginalEditsRecoveryAndSession() =>
        WithNewNote((editor, source, recovery, session, directory) =>
    {
        var destination = directory.File("blocked.md");
        Directory.CreateDirectory(destination);

        var (saved, _, _) = SaveWithDialogDismissed(editor, () => editor.TrySaveCurrentNote(dialog =>
        {
            dialog.FileName = destination;
            return true;
        }));

        Assert.IsFalse(saved);
        AssertOriginalDraft(editor, source, recovery, session);
        Assert.IsTrue(Directory.Exists(destination));
    });

    [TestMethod]
    public void DestinationChangingDuringConfirmationIsRejectedWithoutWriting() =>
        WithNewNote((editor, source, recovery, session, directory) =>
    {
        var destination = directory.File("existing.md");
        File.WriteAllText(destination, "before confirmation");

        var (saved, _, _) = SaveWithDialogDismissed(editor, () => editor.TrySaveCurrentNote(dialog =>
        {
            dialog.FileName = destination;
            return true;
        }, _ =>
        {
            File.WriteAllText(destination, "changed during confirmation");
            return true;
        }));

        Assert.IsFalse(saved);
        Assert.AreEqual("changed during confirmation", File.ReadAllText(destination));
        AssertOriginalDraft(editor, source, recovery, session);
    });

    [TestMethod]
    public void LateFirstSaveConflictPreservesBothDestinationVersionsAndOriginalRecovery() =>
        WithNewNote((editor, source, recovery, session, directory) =>
    {
        var destination = directory.File("existing.md");
        File.WriteAllText(destination, "confirmed content");

        var (saved, _, message) = SaveWithDialogDismissed(editor, () => editor.TrySaveCurrentNote(dialog =>
        {
            dialog.FileName = destination;
            return true;
        }, _ => true));

        Assert.IsFalse(saved);
        var preserved = Directory.GetFiles(directory.Path, "noted-conflict-*.md").Single();
        Assert.AreEqual("late external edits", File.ReadAllText(preserved));
        Assert.AreEqual("editor edits", File.ReadAllText(destination));
        StringAssert.Contains(message!, preserved);
        AssertOriginalDraft(editor, source, recovery, session);
    }, directory => new NoteContentService(directory.Path,
        new CallbackTimeProvider(() => File.WriteAllText(directory.File("existing.md"), "late external edits")), 20));

    [TestMethod]
    public void SavedDocumentSaveAsKeepsItsBuiltInPromptAndOriginalFile() =>
        WithNewNote((editor, source, _, _, directory) =>
    {
        editor.Workspace.MarkSaved(editor.Workspace.CurrentDocument!);
        editor.Workspace.CurrentDocument!.UsesGeneratedName = false;
        var destination = directory.File("existing.md");
        File.WriteAllText(destination, "replace after picker confirmation");

        Assert.IsTrue(editor.TrySaveCurrentNoteAs(dialog =>
        {
            Assert.IsTrue(dialog.OverwritePrompt);
            Assert.AreEqual(NoteFileExtensions.FileDialogFilter, dialog.Filter);
            dialog.FileName = destination;
            return true;
        }));

        Assert.AreEqual("editor edits", File.ReadAllText(destination));
        Assert.AreEqual("", File.ReadAllText(source));
    });

    private static void WithNewNote(Action<NoteEditorWindow, string, NoteRecoveryService,
        NoteEditorSessionService, TemporaryTestDirectory> action,
        Func<TemporaryTestDirectory, INoteContentService>? createContent = null) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app");
        var source = directory.File("2026-11-05_10-20-30.txt");
        File.WriteAllText(source, "");
        var recovery = new NoteRecoveryService(appData);
        var session = new NoteEditorSessionService(appData);
        var content = createContent?.Invoke(directory) ?? new NoteContentService(appData);
        var editor = new NoteEditorWindow(content, new AppSettingsService(appData), recovery, session);
        try
        {
            editor.Show();
            Assert.IsTrue(editor.OpenCreatedNote(source, usesGeneratedName: true, initialContent: ""));
            var box = (TextBox)editor.FindName("EditorTextBox");
            box.SelectedText = "editor edits";
            box.Select(2, 4);
            recovery.SaveDraft(source, box.Text);
            Assert.IsTrue(editor.SaveEditorSession());
            action(editor, source, recovery, session, directory);
        }
        finally
        {
            foreach (var document in editor.Workspace.Documents) editor.Workspace.MarkSaved(document);
            editor.TryPrepareForClose();
            editor.Close();
        }
    });

    private static void AssertOriginalDraft(NoteEditorWindow editor, string source,
        NoteRecoveryService recovery, NoteEditorSessionService session)
    {
        var document = editor.Workspace.CurrentDocument!;
        var box = (TextBox)editor.FindName("EditorTextBox");
        Assert.AreEqual(source, document.FilePath);
        Assert.AreEqual("", File.ReadAllText(source));
        Assert.AreEqual("editor edits", document.Content);
        Assert.AreEqual("editor edits", box.Text);
        Assert.IsTrue(document.IsDirty);
        Assert.IsTrue(document.UsesGeneratedName);
        Assert.AreEqual("", document.InitialFileContent);
        Assert.AreEqual(2, box.SelectionStart);
        Assert.AreEqual(4, box.SelectionLength);
        Assert.AreEqual("editor edits", recovery.LoadDraft(source).Draft!.Content);
        Assert.AreEqual(source, session.Load().Session.ActiveFilePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LateConflictKeepsFocusedPaneDirtyAndRetainsRecoveryUntilExplicitRetry(bool secondary) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var appData = directory.File("app");
        var path = directory.File("first.md");
        var otherPath = directory.File("second.md");
        File.WriteAllText(path, "original");
        File.WriteAllText(otherPath, "other");
        var settings = new AppSettingsService(appData);
        var clock = new CallbackTimeProvider(() => File.WriteAllText(path, "external"));
        var content = new NoteContentService(appData, clock, 20);
        var recovery = new NoteRecoveryService(appData);
        var editor = new NoteEditorWindow(content, settings, recovery, new NoteEditorSessionService(appData));
        try
        {
            editor.Show();
            Assert.IsTrue(editor.OpenNote(path));
            if (secondary)
            {
                Assert.IsTrue(editor.OpenNote(otherPath));
                editor.HandleNavigationShortcut(Key.Oem5, ModifierKeys.Control);
                editor.HandleNavigationShortcut(Key.D2, ModifierKeys.Control);
            }
            var box = (TextBox)editor.FindName(secondary ? "SecondaryEditorTextBox" : "EditorTextBox");
            box.Focus();
            box.Text = "editor edits";
            box.Select(2, 4);
            var document = editor.Workspace.CurrentDocument!;
            Assert.AreEqual(path, document.FilePath);
            recovery.SaveDraft(path, "editor edits");

            var (saved, title, message) = SaveWithDialogDismissed(editor);

            Assert.IsFalse(saved);
            Assert.AreEqual("Note changed outside Noted", title);
            var preserved = Directory.GetFiles(directory.Path, "noted-conflict-*.md").Single();
            StringAssert.Contains(message!, preserved);
            Assert.AreEqual("external", File.ReadAllText(preserved));
            Assert.AreEqual("editor edits", File.ReadAllText(path));
            Assert.AreEqual("editor edits", box.Text);
            Assert.AreEqual(2, box.SelectionStart);
            Assert.AreEqual(4, box.SelectionLength);
            Assert.IsTrue(document.IsDirty);
            Assert.AreEqual("original", document.SavedContent);
            Assert.AreEqual("editor edits", recovery.LoadDraft(path).Draft!.Content);
            if (secondary)
            {
                Assert.AreEqual("other", ((TextBox)editor.FindName("EditorTextBox")).Text);
                Assert.IsFalse(editor.Workspace.ActiveDocument!.IsDirty);
            }

            Assert.IsTrue(SaveCurrentNote(editor));
            Assert.IsFalse(document.IsDirty);
            Assert.IsNull(recovery.LoadDraft(path).Draft);
            Assert.AreEqual("external", File.ReadAllText(preserved));
        }
        finally
        {
            foreach (var document in editor.Workspace.Documents) editor.Workspace.MarkSaved(document);
            editor.TryPrepareForClose();
            editor.Close();
        }
    });

    [TestMethod]
    public void ExternalChangeBeforeSavingKeepsExistingConflictBehavior() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var path = directory.File("note.md");
        File.WriteAllText(path, "original");
        var settings = new AppSettingsService(directory.Path);
        var editor = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
            new NoteRecoveryService(directory.Path), new NoteEditorSessionService(directory.Path));
        try
        {
            editor.Show();
            Assert.IsTrue(editor.OpenNote(path));
            ((TextBox)editor.FindName("EditorTextBox")).Text = "editor edits";
            File.WriteAllText(path, "external before saving");

            var (saved, title, _) = SaveWithDialogDismissed(editor);

            Assert.IsFalse(saved);
            Assert.AreEqual("Note changed outside Noted", title);
            Assert.AreEqual("external before saving", File.ReadAllText(path));
            Assert.IsTrue(editor.Workspace.CurrentDocument!.IsDirty);
            Assert.HasCount(0, Directory.GetFiles(directory.Path, "noted-conflict-*.md"));
        }
        finally
        {
            foreach (var document in editor.Workspace.Documents) editor.Workspace.MarkSaved(document);
            editor.TryPrepareForClose();
            editor.Close();
        }
    });

    private static bool SaveCurrentNote(NoteEditorWindow editor) =>
        editor.TrySaveCurrentNote();

    private static (bool Saved, string? Title, string? Message) SaveWithDialogDismissed(
        NoteEditorWindow editor, Func<bool>? save = null)
    {
        string? title = null;
        string? message = null;
        var dismiss = editor.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<AppDialog>().FirstOrDefault();
            if (dialog is null) return;
            title = dialog.Title;
            message = ((TextBlock)dialog.FindName("MessageText")).Text;
            dialog.Close();
        }, DispatcherPriority.ApplicationIdle);
        try { return (save is null ? SaveCurrentNote(editor) : save(), title, message); }
        finally { dismiss.Abort(); }
    }
}
