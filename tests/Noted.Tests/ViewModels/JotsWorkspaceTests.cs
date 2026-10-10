using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class JotsWorkspaceTests
{
    [TestMethod]
    public void EditorsShareItemsAndCollectionReflectsEditsWithIndependentFilters() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Title = "First" }, new JotItemState { Title = "Second" }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var collection = new JotsWindowViewModel(workspace, false);
        using var firstEditor = new JotsWindowViewModel(workspace, false);
        using var secondEditor = new JotsWindowViewModel(workspace, false);
        firstEditor.OpenItem(workspace.Items[0]);
        secondEditor.OpenItem(workspace.Items[1]);
        firstEditor.SearchText = "First";
        Assert.AreEqual(2, collection.FilteredItems.Cast<object>().Count());
        firstEditor.BeginEditSelected();
        secondEditor.BeginEditSelected();
        firstEditor.SelectedItem!.DraftCategory = "Work";
        secondEditor.SelectedItem!.DraftDetails = "Other details";
        Assert.IsTrue(firstEditor.TrySaveSelected(out var error), error);
        Assert.IsTrue(secondEditor.TrySaveSelected(out error), error);
        Assert.AreSame(firstEditor.SelectedItem, collection.Items[0]);
        CollectionAssert.Contains(collection.Categories.ToArray(), "Work");
        Assert.IsTrue(workspace.TryFlush(out error), error);
        Assert.AreEqual("Other details", content.LoadItems().Items[1].Details);
    });

    [TestMethod]
    public void RemovingAnOpenItemCancelsItsDraftAndCannotRecreateIt() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Title = "Delete me" }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using var collection = new JotsWindowViewModel(workspace, false);
        using var editor = new JotsWindowViewModel(workspace, false);
        var item = workspace.Items.Single();
        editor.OpenItem(item);
        editor.BeginEditSelected();
        item.DraftDetails = "Uncommitted edit";
        collection.RemoveItem(item);
        Assert.IsNull(editor.SelectedItem);
        Assert.IsFalse(item.IsEditing);
        Assert.IsTrue(editor.TrySaveSelected(out var error), error);
        Assert.IsTrue(workspace.TryFlush(out error), error);
        Assert.AreEqual(0, content.LoadItems().Items.Count);
    });

    [TestMethod]
    public void ClosingAViewDoesNotDisposeSharedWorkspaceOrLosePendingEdits() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        using (var collection = new JotsWindowViewModel(workspace, false))
        {
            var item = collection.BeginNewItem();
            item.DraftDetails = "Saved after view closes";
            Assert.IsTrue(collection.TrySaveSelected(out var error), error);
        }
        Assert.IsTrue(workspace.TryFlush(out var flushError), flushError);
        Assert.AreEqual("Saved after view closes", content.LoadItems().Items.Single().Details);
    });
}
