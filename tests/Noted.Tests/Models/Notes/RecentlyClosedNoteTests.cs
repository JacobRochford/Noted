using System.IO;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;

namespace Noted.Tests.Models;

[TestClass]
public sealed class RecentlyClosedNoteTests
{
    [TestMethod]
    public void ReopensInReverseCloseOrderAndConsumesSuccessfulEntries()
    {
        using var directory = new TemporaryTestDirectory();
        var workspace = new NoteEditorWorkspace();
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            workspace.Close(Add(workspace, directory.File(name)), rememberClosed: true);
        var reopened = new List<string>();
        while (workspace.ReopenClosedTab(tab => { reopened.Add(Path.GetFileName(tab.State.FilePath)); return true; })) { }
        CollectionAssert.AreEqual(new[] { "c.txt", "b.txt", "a.txt" }, reopened);
        Assert.AreEqual(0, workspace.ClosedTabCount);
    }

    [TestMethod]
    public void KeepsOnlyTwentyMostRecentEntriesAndDeduplicatesPaths()
    {
        using var directory = new TemporaryTestDirectory();
        var workspace = new NoteEditorWorkspace();
        for (var i = 0; i < 22; i++)
            workspace.Close(Add(workspace, directory.File($"{i}.txt")), rememberClosed: true);
        workspace.Close(Add(workspace, directory.File("21.txt")), rememberClosed: true);
        Assert.AreEqual(20, workspace.ClosedTabCount);
        var reopened = new List<string>();
        while (workspace.ReopenClosedTab(tab => { reopened.Add(Path.GetFileName(tab.State.FilePath)); return true; })) { }
        Assert.AreEqual("21.txt", reopened[0]);
        Assert.AreEqual("2.txt", reopened[^1]);
    }

    [TestMethod]
    public void SkipsDeletedAndAlreadyOpenFilesButRetainsFailedReopen()
    {
        using var directory = new TemporaryTestDirectory();
        var workspace = new NoteEditorWorkspace();
        var valid = Add(workspace, directory.File("valid.txt"));
        workspace.Close(valid, rememberClosed: true);
        var open = Add(workspace, directory.File("open.txt"));
        workspace.Close(open, rememberClosed: true);
        workspace.Add(open);
        var deleted = Add(workspace, directory.File("deleted.txt"));
        workspace.Close(deleted, rememberClosed: true);
        File.Delete(deleted.FilePath);
        Assert.IsFalse(workspace.ReopenClosedTab(_ => false));
        Assert.AreEqual(1, workspace.ClosedTabCount);
        Assert.IsTrue(workspace.ReopenClosedTab(tab => tab.State.FilePath == valid.FilePath));
        Assert.IsFalse(workspace.ReopenClosedTab(_ => throw new AssertFailedException("History must be empty.")));
    }

    [TestMethod]
    public void NeverSavedNotesAreExcludedAndSavedNewNotesBecomeEligible()
    {
        using var directory = new TemporaryTestDirectory();
        var workspace = new NoteEditorWorkspace();
        var document = Add(workspace, directory.File("new.txt"));
        workspace.UpdateCreationMetadata(document, true, "disk");
        workspace.Close(document, rememberClosed: true);
        Assert.AreEqual(0, workspace.ClosedTabCount);
        workspace.Add(document);
        workspace.MarkSaved(document);
        workspace.Close(document, rememberClosed: true);
        Assert.AreEqual(1, workspace.ClosedTabCount);
    }

    [TestMethod]
    public void ExplicitHistoryIsSeparateFromSessionAndBulkClose()
    {
        using var directory = new TemporaryTestDirectory();
        var workspace = new NoteEditorWorkspace();
        var document = Add(workspace, directory.File("note.md"));
        var partner = Add(workspace, directory.File("partner.txt"));
        workspace.Activate(partner);
        workspace.ShowSecondary(document, focus: true);
        workspace.UpdateViewState(document, 2, 15);
        workspace.SetMarkdownPreviewMode(document, MarkdownPreviewMode.Preview);
        workspace.SetEncoding(document, TextFileEncoding.Utf16BE);
        workspace.Close(document, rememberClosed: true);
        var session = NoteEditorPersistence.CreateSession(workspace);
        Assert.AreEqual(1, session.Tabs.Count);
        Assert.AreEqual(partner.FilePath, session.Tabs[0].FilePath);
        workspace.Close(partner);
        Assert.AreEqual(1, workspace.ClosedTabCount);
        Assert.IsTrue(workspace.ReopenClosedTab(tab =>
        {
            Assert.AreEqual(2, tab.State.CaretIndex);
            Assert.AreEqual(15d, tab.State.VerticalOffset);
            Assert.AreEqual(MarkdownPreviewMode.Preview, tab.State.MarkdownPreviewMode);
            Assert.IsNull(tab.State.PendingEncoding);
            Assert.AreEqual(partner.FilePath, tab.PrimaryPath);
            Assert.AreEqual(document.FilePath, tab.SecondaryPath);
            return true;
        }));
    }

    private static OpenNoteDocument Add(NoteEditorWorkspace workspace, string path)
    {
        File.WriteAllText(path, "disk");
        var document = new OpenNoteDocument(path, "disk", "disk", isDirty: false);
        workspace.Add(document);
        return document;
    }
}
