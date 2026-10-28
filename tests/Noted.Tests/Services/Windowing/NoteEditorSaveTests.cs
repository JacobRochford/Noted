using System.IO;
using System.Reflection;
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
        (bool)typeof(NoteEditorWindow).GetMethod("TrySaveCurrentNote", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null)!;

    private static (bool Saved, string? Title, string? Message) SaveWithDialogDismissed(NoteEditorWindow editor)
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
        try { return (SaveCurrentNote(editor), title, message); }
        finally { dismiss.Abort(); }
    }
}
