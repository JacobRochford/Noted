using System.Runtime.ExceptionServices;
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
