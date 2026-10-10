using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class JotsWindowViewModelTests
{
    [TestMethod]
    public void NewDraftIsNotPersistedUntilSaved() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new JotsContentService(directory.Path);
        using var viewModel = CreateViewModel(service);
        var draft = viewModel.BeginNewItem();
        draft.DraftTitle = "Remember this";
        draft.DraftDetails = "Some details";
        draft.DraftCategory = "Reference";

        Assert.IsTrue(viewModel.TryFlushPendingItems(out var error), error);
        Assert.AreEqual(0, service.LoadItems().Items.Count);

        Assert.IsTrue(viewModel.TrySaveSelected(out error), error);
        Assert.IsTrue(viewModel.TryFlushPendingItems(out error), error);
        var saved = service.LoadItems().Items.Single();
        Assert.AreEqual("Remember this", saved.Title);
        Assert.AreEqual("Some details", saved.Details);
        Assert.AreEqual("Reference", saved.Category);
    });

    [TestMethod]
    public void SearchAndViewsFilterCommittedContent() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new JotsContentService(directory.Path);
        service.SaveItems([
            new JotItemState { Title = "Kestrel", Details = "ASP.NET hosting", Category = "Development", IsPinned = true },
            new JotItemState { Title = "PSU", Details = "Hardware reference", Category = "" },
            new JotItemState { Title = "Golf thought", Details = "Try this later", Category = "Golf" }
        ]);
        using var viewModel = CreateViewModel(service);

        CollectionAssert.AreEquivalent(
            new[] { "Development", "Golf" },
            viewModel.Categories.ToArray());

        viewModel.SelectInbox();
        Assert.AreEqual("PSU", ((JotItem)viewModel.FilteredItems.Cast<object>().Single()).Title);

        viewModel.SelectPinned();
        Assert.AreEqual("Kestrel", ((JotItem)viewModel.FilteredItems.Cast<object>().Single()).Title);

        viewModel.SelectCategory("Golf");
        Assert.AreEqual("Golf thought", ((JotItem)viewModel.FilteredItems.Cast<object>().Single()).Title);

        viewModel.SelectAll();
        viewModel.SearchText = "hosting";
        Assert.AreEqual("Kestrel", ((JotItem)viewModel.FilteredItems.Cast<object>().Single()).Title);
    });

    [TestMethod]
    public void QuickPinAndStarPersistAndRefreshPinnedView() => RunSta(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new JotsContentService(directory.Path);
        service.SaveItems([new JotItemState { Title = "Keep me" }]);
        using var viewModel = CreateViewModel(service);
        var item = viewModel.Items.Single();
        viewModel.SelectPinned();
        Assert.IsTrue(viewModel.FilteredItems.IsEmpty);

        viewModel.TogglePinned(item);
        viewModel.ToggleStarred(item);

        Assert.IsFalse(viewModel.FilteredItems.IsEmpty);
        Assert.IsTrue(viewModel.TryFlushPendingItems(out var error), error);
        var saved = service.LoadItems().Items.Single();
        Assert.IsTrue(saved.IsPinned);
        Assert.IsTrue(saved.IsStarred);
    });

    private static JotsWindowViewModel CreateViewModel(IJotsContentService service) =>
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Jots test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
