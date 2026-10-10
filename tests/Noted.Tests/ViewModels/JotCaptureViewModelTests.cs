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
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual("Keep me", capture.Text);
        Assert.IsNotNull(capture.Error);
        Assert.AreEqual(0, workspace.Items.Count);
        using (var recovered = CreateCapture(workspace, directory.Path))
            Assert.AreEqual("Keep me", recovered.Text);
        content.FailSave = false;
        Assert.IsTrue(capture.TrySave());
        Assert.IsFalse(capture.TrySave());
        Assert.AreEqual(1, content.Saved.Count);
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
            Assert.IsFalse(capture.TrySave());
            Assert.AreEqual(1, content.Saved.Count);
            Assert.AreEqual("Saved once", capture.Text);
            Assert.IsTrue(capture.IsReadOnly);
            capture.Text = "Do not replace the committed draft";
            Assert.AreEqual("Saved once", capture.Text);
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
