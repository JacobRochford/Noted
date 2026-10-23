using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class DictionaryWindowViewModelTests
{
    [TestMethod]
    public void NewEntryAutoSavesContentWithoutPersistingEmptyPlaceholders() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new DictionaryContentService(directory.Path);
        using var viewModel = CreateViewModel(service);
        var item = viewModel.AddItem(beginEdit: true, autoSave: true);
        Assert.IsTrue(viewModel.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(0, service.LoadItems().Items.Count);

        item.DraftWord = "word";
        item.DraftDefinition = "raw *Markdown*";
        Assert.IsTrue(viewModel.TryFlushPendingItems(out error), error);
        var saved = service.LoadItems().Items.Single();
        Assert.AreEqual("word", saved.Word);
        Assert.AreEqual("raw *Markdown*", saved.Description);

        item.DraftWord = "";
        item.DraftDefinition = "";
        Assert.IsTrue(viewModel.TryFlushPendingItems(out error), error);
        Assert.AreEqual(0, service.LoadItems().Items.Count);
    });

    [TestMethod]
    public void SavePersistsRawMarkdownWhileOtherDraftsRemainUnsaved() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new DictionaryContentService(directory.Path);
        service.SaveItems([
            new DictionaryItemState { Word = "first", Description = "saved first" },
            new DictionaryItemState { Word = "second", Description = "saved second" }
        ]);
        using var viewModel = CreateViewModel(service);
        var first = viewModel.Items[0];
        var second = viewModel.Items[1];
        first.BeginEdit();
        first.DraftDefinition = "**first draft**";
        second.IsExpanded = true;
        second.BeginEdit();
        second.DraftDefinition = "# second saved";
        second.SaveEdit();
        Assert.IsTrue(viewModel.TryFlushPendingItems(out var error), error);

        var saved = new DictionaryContentService(directory.Path).LoadItems().Items;
        Assert.AreEqual("saved first", saved[0].Description);
        Assert.AreEqual("# second saved", saved[1].Description);
        first.BeginEdit();
        Assert.AreEqual("**first draft**", first.DraftDefinition);
        first.CancelEdit();
        Assert.AreEqual("saved first", first.DraftDefinition);
        Assert.IsFalse(first.IsEditing);
    });

    [TestMethod]
    public void NewDraftIsExcludedUntilSaveAndCancelRemovesIt() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new DictionaryContentService(directory.Path);
        using var viewModel = CreateViewModel(service);
        var draft = viewModel.AddItem(beginEdit: true);
        draft.DraftWord = "word";
        draft.DraftDefinition = "raw *Markdown*";
        Assert.IsTrue(viewModel.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(0, service.LoadItems().Items.Count);

        draft.SaveEdit();
        Assert.IsTrue(viewModel.TryFlushPendingItems(out error), error);
        var saved = service.LoadItems().Items.Single();
        Assert.AreEqual("word", saved.Word);
        Assert.AreEqual("raw *Markdown*", saved.Description);

        var cancelled = viewModel.AddItem(beginEdit: true);
        cancelled.DraftWord = "cancelled";
        cancelled.CancelEdit();
        if (cancelled.IsNew) viewModel.RemoveItem(cancelled);
        Assert.IsTrue(viewModel.TryFlushPendingItems(out error), error);
        Assert.AreEqual(1, service.LoadItems().Items.Count);
    });

    [TestMethod]
    public void SearchAndDeletionDoNotPublishDrafts() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new DictionaryContentService(directory.Path);
        service.SaveItems([new DictionaryItemState { Word = "word", Description = "saved" }]);
        using var viewModel = CreateViewModel(service);
        var item = viewModel.Items.Single();
        item.BeginEdit();
        item.DraftDefinition = "unsaved";
        viewModel.SearchText = "no match";
        Assert.IsTrue(viewModel.FilteredItems.IsEmpty);
        viewModel.SearchText = "saved";
        Assert.IsFalse(viewModel.FilteredItems.IsEmpty);
        Assert.AreEqual("unsaved", item.DraftDefinition);
        Assert.IsTrue(item.IsEditing);

        viewModel.RemoveItem(item);
        Assert.AreEqual(0, viewModel.Items.Count);
        Assert.IsTrue(viewModel.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(0, service.LoadItems().Items.Count);
    });

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void FailedSavesKeepTheAcceptedBaselineAndCancelWithdrawsTheCandidate(int failures) => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path) { FailuresRemaining = failures };
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        item.BeginEdit();
        model.SearchText = "original";
        var original = File.ReadAllBytes(directory.File("dictionary.json"));

        for (var attempt = 0; attempt < failures; attempt++)
        {
            item.DraftWord = $"attempt {attempt}";
            item.DraftDefinition = $"definition {attempt}";
            Assert.IsFalse(model.TrySaveEntry(item, out var error));
            Assert.IsNotNull(error);
            Assert.AreEqual("original", item.Word);
            Assert.AreEqual("original definition", item.Definition);
            Assert.AreEqual($"attempt {attempt}", item.DraftWord);
            Assert.AreEqual($"definition {attempt}", item.DraftDefinition);
            Assert.IsTrue(item.IsEditing && item.HasPendingEdit && item.ShowEditActions);
            Assert.IsTrue(model.HasPersistenceError);
            Assert.IsFalse(model.FilteredItems.IsEmpty);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(directory.File("dictionary.json")));
        }

        item.CancelEdit();
        Assert.AreEqual("original", item.DraftWord);
        Assert.AreEqual("original definition", item.DraftDefinition);
        Assert.IsFalse(item.IsEditing);
        Assert.IsTrue(model.HasPersistenceError, "Cancel must not clear an unresolved persistence failure.");
        Assert.IsTrue(model.TryFlushPendingItems(out var flushError), flushError);
        Assert.AreEqual("original", service.Persisted[0].Word);
        Assert.AreEqual("original definition", service.Persisted[0].Description);
        using var restarted = CreateViewModel(new DictionaryContentService(directory.Path));
        Assert.AreEqual("original", restarted.Items[0].Word);
        Assert.AreEqual("original definition", restarted.Items[0].Definition);
    });

    [TestMethod]
    public void OtherSavesAndAutosavesExcludeTheFailedDraftUntilAnotherExplicitSave() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path) { FailuresRemaining = 1 };
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        var other = model.Items[1];
        var added = model.AddItem(beginEdit: true, autoSave: true);
        added.DraftWord = "authorized new entry";
        item.BeginEdit();
        item.DraftWord = "failed word";
        item.DraftDefinition = "failed definition";
        Assert.IsFalse(model.TrySaveEntry(item, out _));

        item.DraftWord = "later word";
        item.DraftDefinition = "later definition";
        added.DraftDefinition = "latest autosave";
        other.BeginEdit();
        other.DraftDefinition = "authorized other save";
        Assert.IsTrue(model.TrySaveEntry(other, out var error), error);
        Assert.AreEqual("original", service.Persisted[0].Word);
        Assert.AreEqual("original definition", service.Persisted[0].Description);
        Assert.AreEqual("authorized other save", service.Persisted[1].Description);
        Assert.AreEqual("latest autosave", service.Persisted[2].Description);
        Assert.AreEqual("later definition", item.DraftDefinition);
        Assert.IsTrue(item.HasPendingEdit);

        Assert.IsTrue(model.TrySaveEntry(item, out error), error);
        Assert.AreEqual("later word", item.Word);
        Assert.AreEqual("later definition", item.Definition);
        Assert.IsFalse(item.IsEditing);
        Assert.AreEqual("later definition", service.Persisted[0].Description);
        var writes = service.Attempts.Count;
        Assert.IsTrue(model.TryFlushPendingItems(out error), error);
        Assert.AreEqual(writes, service.Attempts.Count, "Publishing a saved entry must not queue an identical write.");
        using var restarted = CreateViewModel(new DictionaryContentService(directory.Path));
        Assert.AreEqual("later word", restarted.Items[0].Word);
        Assert.AreEqual("latest autosave", restarted.Items[2].Definition);
    });

    [TestMethod]
    public void ReentrantFailureNotificationCannotRetryTheExplicitCandidate() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path) { FailuresRemaining = 1 };
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        item.BeginEdit();
        item.DraftDefinition = "failed candidate";
        var handled = false;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(model.PersistenceError) || !model.HasPersistenceError || handled)
                return;
            handled = true;
            var added = model.AddItem(beginEdit: true, autoSave: true);
            added.DraftWord = "authorized during notification";
            Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        };

        Assert.IsFalse(model.TrySaveEntry(item, out _));

        Assert.IsTrue(handled);
        Assert.HasCount(2, service.Attempts);
        Assert.AreEqual("failed candidate", service.Attempts[0][0].Description);
        Assert.AreEqual("original definition", service.Attempts[1][0].Description);
        Assert.AreEqual("authorized during notification", service.Persisted[2].Word);
        Assert.AreEqual("failed candidate", item.DraftDefinition);
        Assert.IsTrue(item.HasPendingEdit);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReentrantAutosaveCannotQueueOldValuesAfterAnExplicitSave(bool revertChange) => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path);
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        var added = model.AddItem(beginEdit: true, autoSave: true);
        added.DraftWord = "initial autosave";
        item.BeginEdit();
        item.DraftDefinition = "explicit save";
        service.BeforeSave = () =>
        {
            added.DraftWord = "changed during write";
            if (revertChange) added.DraftWord = "initial autosave";
        };

        Assert.IsTrue(model.TrySaveEntry(item, out var error), error);
        Assert.IsTrue(model.TryFlushPendingItems(out error), error);

        Assert.AreEqual("explicit save", service.Persisted[0].Description);
        Assert.AreEqual(revertChange ? "initial autosave" : "changed during write", service.Persisted[2].Word);
        Assert.AreEqual("explicit save", item.Definition);
    });

    [TestMethod]
    public void ChangesDuringCommitNotificationsKeepBothSavedFieldsAndAuthorizedAutosaves() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path);
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        var added = model.AddItem(beginEdit: true, autoSave: true);
        item.BeginEdit();
        item.DraftWord = "saved word";
        item.DraftDefinition = "saved definition";
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(item.Word)) return;
            Assert.AreEqual("saved word", item.Word);
            Assert.AreEqual("saved definition", item.Definition);
            added.DraftWord = "authorized during commit";
        };

        Assert.IsTrue(model.TrySaveEntry(item, out var error), error);
        Assert.IsTrue(model.TryFlushPendingItems(out error), error);

        Assert.AreEqual("saved word", service.Persisted[0].Word);
        Assert.AreEqual("saved definition", service.Persisted[0].Description);
        Assert.AreEqual("authorized during commit", service.Persisted[2].Word);
    });

    [TestMethod]
    public void EditingDuringAWriteKeepsTheLaterDraftUncommitted() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path);
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        item.BeginEdit();
        item.DraftDefinition = "requested save";
        service.BeforeSave = () => item.DraftDefinition = "later unfinished draft";

        Assert.IsTrue(model.TrySaveEntry(item, out var error), error);

        Assert.AreEqual("requested save", item.Definition);
        Assert.AreEqual("later unfinished draft", item.DraftDefinition);
        Assert.IsTrue(item.IsEditing && item.HasPendingEdit);
        Assert.IsTrue(model.TryFlushPendingItems(out error), error);
        Assert.AreEqual("requested save", service.Persisted[0].Description);
        item.CancelEdit();
        Assert.AreEqual("requested save", item.DraftDefinition);
    });

    [TestMethod]
    public void TimerRetryAfterCancelPersistsOnlyAcceptedValuesAndAuthorizedNewEntries() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path) { FailuresRemaining = 1 };
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        var added = model.AddItem(beginEdit: true, autoSave: true);
        added.DraftWord = "keep this autosave";
        item.BeginEdit();
        item.DraftDefinition = "canceled candidate";
        Assert.IsFalse(model.TrySaveEntry(item, out _));
        item.CancelEdit();
        added.DraftDefinition = "latest authorized content";

        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => frame.Continue = service.Attempts.Count < 2 && elapsed.Elapsed < TimeSpan.FromSeconds(5);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }

        Assert.HasCount(2, service.Attempts);
        Assert.AreEqual("original definition", service.Attempts[1][0].Description);
        Assert.AreEqual("original definition", service.Persisted[0].Description);
        Assert.AreEqual("keep this autosave", service.Persisted[2].Word);
        Assert.AreEqual("latest authorized content", service.Persisted[2].Description);
    });

    [TestMethod]
    public void LockedPrimarySaveAndCancelPreservePreviouslyPersistedContent() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path);
        using var model = CreateViewModel(service);
        var primaryPath = directory.File("dictionary.json");
        var backupPath = directory.File("dictionary.json.bak");
        File.Copy(primaryPath, backupPath);
        var primary = File.ReadAllBytes(primaryPath);
        var backup = File.ReadAllBytes(backupPath);
        var item = model.Items[0];
        item.BeginEdit();
        item.DraftWord = "failed candidate";
        using (File.Open(primaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(model.TrySaveEntry(item, out _));
            Assert.IsFalse(service.WritesBlocked, "A failure before replacement must remain retryable.");
            Assert.AreEqual("original", item.Word);
            Assert.AreEqual("failed candidate", item.DraftWord);
            item.CancelEdit();
            Assert.IsFalse(model.TryFlushPendingItems(out _));
            CollectionAssert.AreEqual(primary, File.ReadAllBytes(primaryPath));
            CollectionAssert.AreEqual(backup, File.ReadAllBytes(backupPath));
        }

        Assert.IsTrue(model.TryFlushPendingItems(out var error), error);
        using var restarted = CreateViewModel(new DictionaryContentService(directory.Path));
        Assert.AreEqual("original", restarted.Items[0].Word);
        Assert.AreEqual("original definition", restarted.Items[0].Definition);
    });

    [TestMethod]
    public void VerificationFailureKeepsDiskUncertaintyAndBlocksWritesAfterCancel() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path) { FailVerification = true };
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        item.BeginEdit();
        item.DraftDefinition = "written but not verified";

        Assert.IsFalse(model.TrySaveEntry(item, out var error));

        Assert.IsTrue(service.WritesBlocked);
        Assert.AreEqual("original definition", item.Definition);
        Assert.AreEqual("written but not verified", item.DraftDefinition);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "uncertain");
        StringAssert.Contains(error, "does not undo");
        Assert.AreEqual("written but not verified", service.Persisted[0].Description);
        var written = File.ReadAllBytes(directory.File("dictionary.json"));
        var backup = File.ReadAllBytes(directory.File("dictionary.json.bak"));
        item.CancelEdit();
        Assert.IsTrue(model.HasPersistenceError);
        Assert.IsFalse(model.TryFlushPendingItems(out _));
        Assert.IsTrue(model.HasPersistenceError);
        CollectionAssert.AreEqual(written, File.ReadAllBytes(directory.File("dictionary.json")));
        CollectionAssert.AreEqual(backup, File.ReadAllBytes(directory.File("dictionary.json.bak")));
        using var restarted = CreateViewModel(new DictionaryContentService(directory.Path));
        Assert.AreEqual("written but not verified", restarted.Items[0].Definition);
    });

    [TestMethod]
    public void SuccessfulPrimarySaveWithBackupWarningRemainsCommitted() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new ControlledContentService(directory.Path);
        using var model = CreateViewModel(service);
        var item = model.Items[0];
        var backupPath = directory.File("dictionary.json.bak");
        File.Copy(directory.File("dictionary.json"), backupPath);
        item.BeginEdit();
        item.DraftDefinition = "success with warning";

        using (File.Open(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsTrue(model.TrySaveEntry(item, out var error), error);
            Assert.AreEqual("success with warning", item.Definition);
            Assert.IsFalse(item.IsEditing);
            Assert.IsNotNull(model.PersistenceError);
            StringAssert.Contains(model.PersistenceError, "rolling backup");
        }
        Assert.IsFalse(service.WritesBlocked);
        Assert.AreEqual("success with warning", service.Persisted[0].Description);
        item.BeginEdit();
        item.DraftDefinition = "discard this";
        item.CancelEdit();
        Assert.AreEqual("success with warning", item.Definition);
        Assert.IsTrue(model.TryFlushPendingItems(out _));
        Assert.HasCount(1, service.Attempts);
    });

    private sealed class ControlledContentService : IDictionaryContentService
    {
        private readonly string _directory;
        private readonly JsonCollectionFileStore<DictionaryItemState> _store;
        private int _validationCalls;

        internal ControlledContentService(string directory)
        {
            _directory = directory;
            _store = new JsonCollectionFileStore<DictionaryItemState>(
                Path.Combine(directory, "dictionary.json"), "Dictionary content", _ =>
                {
                    if (FailVerification && ++_validationCalls == 2)
                    {
                        FailVerification = false;
                        throw new JsonException("Simulated post-replacement verification failure.");
                    }
                });
            _store.Write([
                new DictionaryItemState { Word = "original", Description = "original definition" },
                new DictionaryItemState { Word = "other", Description = "other definition" }
            ]);
        }

        internal int FailuresRemaining { get; set; }
        internal bool FailVerification { get; set; }
        internal bool WritesBlocked => _store.WritesBlocked;
        internal Action? BeforeSave { get; set; }
        internal List<IReadOnlyList<DictionaryItemState>> Attempts { get; } = [];
        internal IReadOnlyList<DictionaryItemState> Persisted => new DictionaryContentService(_directory).LoadItems().Items;

        public DictionaryContentLoadResult LoadItems()
        {
            var result = _store.Load();
            return new(result.Items ?? [], result.Issues.Select(issue => new DictionaryContentIssue(issue.FilePath, issue.Message)).ToList());
        }

        public DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items)
        {
            Attempts.Add(items.ToList());
            var callback = BeforeSave;
            BeforeSave = null;
            callback?.Invoke();
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new IOException("Simulated pre-replacement save failure.");
            }
            _validationCalls = 0;
            return new(_store.Write(items));
        }
    }

    private static DictionaryWindowViewModel CreateViewModel(IDictionaryContentService service) =>
        new(service, Dispatcher.CurrentDispatcher, action => action(), false);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Dictionary test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
