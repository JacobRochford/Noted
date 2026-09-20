using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class DictionaryItemTests
{
    [TestMethod]
    public void NewEntryAutosavesBothFieldsAndThenReturnsToProtectedEditing()
    {
        var item = new DictionaryItem();
        item.BeginEdit(autoSave: true);
        item.DraftWord = "new word";
        item.DraftDefinition = "new definition";
        Assert.AreEqual("new word", item.Word);
        Assert.AreEqual("new definition", item.Definition);
        Assert.IsFalse(item.ShowEditActions);
        Assert.IsFalse(item.HasPendingEdit);

        item.FinishAutoSave();
        item.BeginEdit();
        item.DraftWord = "changed";
        Assert.IsTrue(item.ShowEditActions);
        Assert.AreEqual("new word", item.Word);
        item.CancelEdit();
        Assert.AreEqual("new word", item.Word);
    }

    [TestMethod]
    public void CancelLeavesBothSavedFieldsUnchanged()
    {
        var item = new DictionaryItem { Word = "original", Definition = "original definition" };
        item.BeginEdit();
        item.DraftWord = "changed";
        item.DraftDefinition = "changed definition";
        Assert.IsTrue(item.HasPendingEdit);
        Assert.AreEqual("original", item.Word);
        Assert.AreEqual("original definition", item.Definition);
        item.CancelEdit();
        Assert.IsFalse(item.HasPendingEdit);
        Assert.AreEqual("original", item.Word);
        Assert.AreEqual("original definition", item.Definition);
    }

    [TestMethod]
    public void SavePublishesTheCompleteEntryBeforeNotifyingPersistence()
    {
        var item = new DictionaryItem();
        item.BeginEdit();
        item.DraftWord = "word";
        item.DraftDefinition = "definition";
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(DictionaryItem.Word) or nameof(DictionaryItem.Definition))) return;
            Assert.AreEqual("word", item.Word);
            Assert.AreEqual("definition", item.Definition);
        };
        item.SaveEdit();
        Assert.IsFalse(item.IsEditing);
    }
}
