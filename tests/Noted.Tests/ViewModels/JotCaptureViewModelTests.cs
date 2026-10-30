using System.IO;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class JotCaptureViewModelTests
{
    [TestMethod]
    public void OptionalFieldsSurviveRestartAndUseExistingJotNormalization() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using (var capture = CreateCapture(workspace, directory.Path))
        {
            capture.Title = "  A title  ";
            capture.Category = "  Work  ";
            Assert.IsFalse(capture.TrySave());
            Assert.IsTrue(capture.TryFlushDraft(out var error), error);
            Assert.IsFalse(workspace.IsLoaded);
        }
        using var reopened = CreateCapture(workspace, directory.Path);
        Assert.AreEqual("  A title  ", reopened.Title);
        Assert.AreEqual("  Work  ", reopened.Category);
        reopened.Text = "body\r\nunchanged";
        Assert.IsTrue(reopened.TrySave());
        var saved = content.LoadItems().Items.Single();
        Assert.AreEqual("A title", saved.Title);
        Assert.AreEqual("Work", saved.Category);
        Assert.AreEqual("body\r\nunchanged", saved.Details);
        Assert.AreEqual("", reopened.Title);
        Assert.AreEqual("", reopened.Category);
        Assert.IsFalse(new JotCaptureDraftService(directory.Path).Load(out _).HasContent);
    });

    [TestMethod]
    public void CategoryOptionsMatchJotsAndRefreshWithoutChangingTheDraft() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var workspace = new JotsWorkspace(new JotsContentService(directory.Path), Dispatcher.CurrentDispatcher);
        workspace.Items.Add(new JotItem { Category = " Work " });
        workspace.Items.Add(new JotItem { Category = "work" });
        workspace.Items.Add(new JotItem { Category = "" });
        workspace.Items.Add(new JotItem { Category = "Home" });
        using var jots = new JotsWindowViewModel(workspace, false);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Category = "new category";
        capture.RefreshCategories();
        CollectionAssert.AreEqual(new[] { "Home", "Work" }, capture.Categories.ToArray());
        CollectionAssert.AreEqual(jots.Categories.ToArray(), capture.Categories.ToArray());
        workspace.Items[0].Category = "Travel";
        capture.RefreshCategories();
        CollectionAssert.AreEqual(jots.Categories.ToArray(), capture.Categories.ToArray());
        Assert.AreEqual("new category", capture.Category);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EditingRecoveredMetadataKeepsTheAlreadySavedJot(bool editTitle) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "body", Title = "Original", Category = "Work" };
        new JotCaptureDraftService(directory.Path).Save(draft);
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Id = draft.Id, Title = draft.Title, Details = draft.Text, Category = draft.Category }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        if (editTitle)
            capture.Title = "Changed";
        else
            capture.Category = "Home";
        Assert.IsTrue(capture.TrySave());
        var saved = content.LoadItems().Items;
        Assert.HasCount(2, saved);
        Assert.AreEqual("Original", saved.Single(item => item.Id == draft.Id).Title);
        Assert.AreEqual("Work", saved.Single(item => item.Id == draft.Id).Category);
        Assert.AreEqual(editTitle ? "Changed" : "Original", saved.Single(item => item.Id != draft.Id).Title);
        Assert.AreEqual(editTitle ? "Work" : "Home", saved.Single(item => item.Id != draft.Id).Category);
    });

    [TestMethod]
    public void ConflictingSavedMetadataDoesNotClearRecoveredDraft() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "body", Title = "Draft title", Category = "Work" };
        new JotCaptureDraftService(directory.Path).Save(draft);
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Id = draft.Id, Title = "Other title", Details = draft.Text, Category = "Home" }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual(draft, new JotCaptureDraftService(directory.Path).Load(out _));
        Assert.AreEqual("Other title", content.LoadItems().Items.Single().Title);
    });

    [TestMethod]
    public void OpeningAndPersistingDraftDoesNotLoadSavedCollection() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new ControlledJotsContentService();
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using (var capture = CreateCapture(workspace, directory.Path))
        {
            capture.Text = "First line\r\nSecond line";
            Assert.IsTrue(capture.TryFlushDraft(out var error), error);
            Assert.AreEqual(0, content.LoadCalls);
        }
        using var reopened = CreateCapture(workspace, directory.Path);
        Assert.AreEqual("First line\r\nSecond line", reopened.Text);
        Assert.AreEqual(0, content.LoadCalls);
    });

    [TestMethod]
    public void CapturePreservesMultilineTextAndClearsDraftOnlyAfterSave() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Text = "\r\n# Remember this\r\n  Keep this formatting\r\n";
        Assert.IsTrue(capture.TrySave());
        Assert.AreEqual("", capture.Text);
        Assert.IsFalse(capture.TrySave());
        var saved = content.LoadItems().Items.Single();
        Assert.AreEqual("Remember this", saved.Title);
        Assert.AreEqual("\r\n# Remember this\r\n  Keep this formatting\r\n", saved.Details);
        Assert.AreEqual("", saved.Category);
        using var reopened = CreateCapture(workspace, directory.Path);
        Assert.AreEqual("", reopened.Text);
    });

    [TestMethod]
    public void FailedCaptureKeepsDraftAndRetryCreatesOneItem() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new ControlledJotsContentService { FailSave = true };
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Text = "Keep me";
        capture.Title = "Keep title";
        capture.Category = "Keep category";
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual("Keep me", capture.Text);
        Assert.AreEqual("Keep title", capture.Title);
        Assert.AreEqual("Keep category", capture.Category);
        Assert.IsNotNull(capture.Error);
        Assert.AreEqual(0, workspace.Items.Count);
        using (var recovered = CreateCapture(workspace, directory.Path))
            Assert.AreEqual("Keep me", recovered.Text);
        content.FailSave = false;
        Assert.IsTrue(capture.TrySave());
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual(1, content.Saved.Count);
        Assert.AreEqual("Keep title", content.Saved.Single().Title);
        Assert.AreEqual("Keep category", content.Saved.Single().Category);
    });

    [TestMethod]
    public void RestartBetweenContentSaveAndDraftClearDoesNotDuplicateCapture() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "Already saved" };
        new JotCaptureDraftService(directory.Path).Save(draft);
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Id = draft.Id, Title = "Already saved", Details = draft.Text }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        Assert.IsTrue(capture.TrySave());
        Assert.AreEqual(1, content.LoadItems().Items.Count);
        Assert.AreEqual("", capture.Text);
    });

    [TestMethod]
    public void EditingRecoveredCaptureUsesNewIdentityAndDoesNotLoseNewText() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "Already saved" };
        new JotCaptureDraftService(directory.Path).Save(draft);
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Id = draft.Id, Details = draft.Text }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Text = "A different thought";
        Assert.IsTrue(capture.TrySave());
        Assert.AreEqual(2, content.LoadItems().Items.Count);
        Assert.IsTrue(content.LoadItems().Items.Any(item => item.Details == "A different thought"));
    });

    [TestMethod]
    public void WhitespaceDoesNotLoadOrWriteSavedJots() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new ControlledJotsContentService();
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Text = " \r\n\t";
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual(0, content.LoadCalls);
        Assert.AreEqual(0, content.Saved.Count);
    });

    [TestMethod]
    public void DraftWriteFailurePreventsContentSubmission() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new ControlledJotsContentService();
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Text = "Still here";
        Directory.CreateDirectory(directory.File("jot-capture-draft.json"));
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual("Still here", capture.Text);
        Assert.AreEqual(0, content.LoadCalls);
        Assert.IsNotNull(capture.Error);
    });

    private static JotCaptureViewModel CreateCapture(JotsWorkspace workspace, string directory) =>
        new(workspace, new JotCaptureDraftService(directory), Dispatcher.CurrentDispatcher);

    [TestMethod]
    public void FailedDraftClearKeepsSavedIdentityAndRetryDoesNotSubmitAgain() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new ControlledJotsContentService();
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        FileStream? draftLock = null;
        content.AfterSave = () => draftLock = File.Open(directory.File("jot-capture-draft.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            capture.Text = "Saved once";
            capture.Title = "Saved title";
            capture.Category = "Saved category";
            Assert.IsFalse(capture.TrySave());
            Assert.AreEqual(1, content.Saved.Count);
            Assert.AreEqual("Saved once", capture.Text);
            Assert.IsTrue(capture.IsReadOnly);
            capture.Text = "Do not replace the committed draft";
            capture.Title = "Do not replace title";
            capture.Category = "Do not replace category";
            Assert.AreEqual("Saved once", capture.Text);
            Assert.AreEqual("Saved title", capture.Title);
            Assert.AreEqual("Saved category", capture.Category);
            draftLock!.Dispose();
            draftLock = null;
            content.AfterSave = null;
            Assert.IsTrue(capture.TrySave());
            Assert.AreEqual(1, content.Saved.Count);
            Assert.AreEqual("", capture.Text);
            Assert.IsFalse(capture.IsReadOnly);
        }
        finally { draftLock?.Dispose(); }
    });

    [TestMethod]
    public void MatchingInMemoryJotMustBeSavedBeforeDraftClear() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var draft = new JotCaptureDraft { Text = "pending committed text" };
        new JotCaptureDraftService(directory.Path).Save(draft);
        var content = new ControlledJotsContentService();
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        workspace.Items.Add(new JotItem { Id = draft.Id, Details = draft.Text });
        using var capture = CreateCapture(workspace, directory.Path);
        content.FailSave = true;
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual(draft.Text, capture.Text);
        Assert.AreEqual(draft, new JotCaptureDraftService(directory.Path).Load(out _));
        content.FailSave = false;
        Assert.IsTrue(capture.TrySave());
        Assert.HasCount(1, content.Saved);
        Assert.AreEqual("", capture.Text);
    });

    [TestMethod]
    public void FailedFallbackClearKeepsSubmittedDraftAndRestartDoesNotDuplicate() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        new JotCaptureDraftService(directory.Path).Save(new JotCaptureDraft());
        var content = new ControlledJotsContentService();
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        FileStream? backupLock = null;
        content.AfterSave = () => backupLock = File.Open(directory.File("jot-capture-draft.json.bak"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            capture.Text = "submitted once";
            Assert.IsFalse(capture.TrySave());
            Assert.IsTrue(capture.IsReadOnly);
            Assert.AreEqual("submitted once", capture.Text);
        }
        finally { backupLock?.Dispose(); }
        content.AfterSave = null;
        using var restartedWorkspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var restarted = CreateCapture(restartedWorkspace, directory.Path);
        Assert.IsTrue(restarted.TrySave());
        Assert.HasCount(1, content.Saved);
        File.Delete(directory.File(PersistencePaths.JotCaptureDraftFile));
        Assert.AreEqual("", new JotCaptureDraftService(directory.Path).Load(out _).Text);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SuccessfulSubmissionCannotReappearFromRollingBackup(bool corruptPrimary) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var capture = CreateCapture(workspace, directory.Path);
        capture.Text = "durably submitted";
        Assert.IsTrue(capture.TrySave());
        var primary = directory.File(PersistencePaths.JotCaptureDraftFile);
        if (corruptPrimary)
            File.WriteAllText(primary, "{broken");
        else
            File.Delete(primary);
        Assert.AreEqual("", new JotCaptureDraftService(directory.Path).Load(out _).Text);
        Assert.AreEqual("durably submitted", content.LoadItems().Items.Single().Details);
    });

    private sealed class ControlledJotsContentService : IJotsContentService
    {
        internal int LoadCalls { get; private set; }
        internal bool FailSave { get; set; }
        internal Action? AfterSave { get; set; }
        internal IReadOnlyList<JotItemState> Saved { get; private set; } = [];
        public JotsContentLoadResult LoadItems()
        {
            LoadCalls++;
            return new(Saved, []);
        }
        public JotsContentSaveResult SaveItems(IReadOnlyList<JotItemState> items)
        {
            if (FailSave) throw new IOException("Simulated save failure.");
            Saved = items.ToList();
            AfterSave?.Invoke();
            return new(null);
        }
    }
}
