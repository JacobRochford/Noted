using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class ManagedNoteDraftWindowTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BothSaveFlowsPromoteManagedDraftsAndPersistThePermanentPath(bool saveAs) =>
        WithDraft((editor, path, recovery, session, files) =>
    {
        var destination = Path.Combine(files.NotesDirectory, "chosen.json");
        bool? Choose(Microsoft.Win32.SaveFileDialog dialog)
        {
            Assert.IsFalse(dialog.OverwritePrompt);
            Assert.AreEqual(files.CurrentDirectory, dialog.InitialDirectory);
            Assert.AreEqual(Path.GetFileName(path)[..19], dialog.FileName);
            dialog.FileName = destination;
            return true;
        }
        Assert.IsTrue(saveAs ? editor.TrySaveCurrentNoteAs(Choose) : editor.TrySaveCurrentNote(Choose));
        Assert.AreEqual(destination, editor.Workspace.CurrentDocument!.FilePath);
        Assert.AreEqual("user edits", File.ReadAllText(destination));
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(NoteDraftStorage.GetMetadataPath(path)));
        Assert.IsNull(recovery.LoadDraft(path).Draft);
        Assert.AreEqual(destination, session.Load().Session.ActiveFilePath);
        Assert.AreEqual("chosen.json", files.GetNotes().Single().FileName);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancelingEitherSaveFlowPreservesTheManagedDraft(bool saveAs) =>
        WithDraft((editor, path, recovery, session, _) =>
    {
        Assert.IsFalse(saveAs ? editor.TrySaveCurrentNoteAs(_ => false) : editor.TrySaveCurrentNote(_ => false));
        Assert.AreEqual(path, editor.Workspace.CurrentDocument!.FilePath);
        Assert.AreEqual("user edits", editor.Workspace.CurrentDocument.Content);
        Assert.AreEqual("user edits", recovery.LoadDraft(path).Draft!.Content);
        Assert.AreEqual(path, session.Load().Session.ActiveFilePath);
        Assert.IsTrue(File.Exists(path));
    });

    [TestMethod]
    public void SavingInsideManagedDraftStorageIsRejected() => WithDraft((editor, path, recovery, _, _) =>
    {
        var operation = editor.Dispatcher.BeginInvoke(() => Application.Current.Windows.OfType<AppDialog>().Single().Close(),
            DispatcherPriority.ApplicationIdle);
        try
        {
            Assert.IsFalse(editor.TrySaveCurrentNote(dialog => { dialog.FileName = path; return true; },
                _ => throw new AssertFailedException("Internal locations must be rejected before asking to replace them.")));
            Assert.AreEqual(path, editor.Workspace.CurrentDocument!.FilePath);
            Assert.IsTrue(editor.Workspace.CurrentDocument.IsDirty);
            Assert.AreEqual("user edits", recovery.LoadDraft(path).Draft!.Content);
            Assert.IsTrue(File.Exists(path));
        }
        finally { operation.Abort(); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClosingEditedTimestampDraftRequiresDiscardOrCancel(bool discard) =>
        WithDraft((editor, path, recovery, _, _) =>
    {
        var prompts = 0;
        var operation = editor.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<AppDialog>().Single();
            prompts++;
            ((StackPanel)dialog.FindName("ButtonsPanel")).Children.OfType<Button>()
                .Single(button => (string)button.Content == (discard ? "No" : "Cancel"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.ApplicationIdle);
        try
        {
            var closed = (bool)typeof(NoteEditorWindow).GetMethod("TryCloseDocument", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(editor, [editor.Workspace.CurrentDocument])!;
            Assert.AreEqual(discard, closed);
            Assert.AreEqual(1, prompts);
            Assert.AreEqual(!discard, File.Exists(path));
            Assert.AreEqual(discard, recovery.LoadDraft(path).Draft is null);
        }
        finally { operation.Abort(); }
    });

    [TestMethod]
    public void UntouchedTimestampDraftClosesWithoutPromptOrLeftoverFiles() =>
        WithDraft((editor, path, recovery, session, _) =>
    {
        Assert.IsFalse(editor.Workspace.CurrentDocument!.IsDirty);
        Assert.IsTrue((bool)typeof(NoteEditorWindow).GetMethod("TryCloseDocument", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, [editor.Workspace.CurrentDocument])!);
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(File.Exists(NoteDraftStorage.GetMetadataPath(path)));
        Assert.IsNull(recovery.LoadDraft(path).Draft);
        Assert.IsEmpty(session.Load().Session.Tabs);
    }, edited: false);

    [TestMethod]
    public void HidingTheEditorFlushesAndKeepsTheDraft() => WithDraft((editor, path, recovery, session, _) =>
    {
        ((TextBox)editor.FindName("EditorTextBox")).Text = "latest edits before hiding";
        editor.Close();
        Assert.IsFalse(editor.IsVisible);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual("latest edits before hiding", recovery.LoadDraft(path).Draft!.Content);
        Assert.AreEqual(path, session.Load().Session.ActiveFilePath);
    });

    private static void WithDraft(Action<NoteEditorWindow, string, NoteRecoveryService, NoteEditorSessionService, NoteFileService> action,
        bool edited = true) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.File("app"));
        settings.SaveNotesDirectory(directory.File("notes"));
        settings.SaveTimestampPlacement(NoteTimestampPlacement.Top);
        using var files = new NoteFileService(settings);
        var created = files.CreateNote().Note!;
        var recovery = new NoteRecoveryService(settings.AppDataDirectory);
        var session = new NoteEditorSessionService(settings.AppDataDirectory);
        var editor = new NoteEditorWindow(new NoteContentService(settings.AppDataDirectory), settings, recovery, session, files);
        try
        {
            Assert.IsTrue(editor.OpenCreatedNote(created.FilePath!, true, created.InitialContent));
            if (edited)
            {
                ((TextBox)editor.FindName("EditorTextBox")).Text = "user edits";
                recovery.SaveDraft(created.FilePath!, "user edits");
            }
            editor.SaveEditorSession();
            action(editor, created.FilePath!, recovery, session, files);
        }
        finally
        {
            editor.CloseAllDocuments();
            editor.TryPrepareForClose();
            editor.Close();
        }
    });
}
