using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class DictionaryItemTests
{
    [TestMethod]
    public void NewEntryAutoSavesButLaterEditsRequireSaveOrCancel()
    {
        var item = new DictionaryItem();
        item.BeginEdit(autoSave: true);
        item.DraftWord = "new word";
        item.DraftDefinition = "# definition";
        Assert.AreEqual("new word", item.Word);
        Assert.AreEqual("# definition", item.Definition);
        Assert.IsFalse(item.HasPendingEdit);
        Assert.IsFalse(item.ShowEditActions);

        item.FinishAutoSave();
        item.BeginEdit();
        item.DraftDefinition = "unsaved edit";
        Assert.AreEqual("# definition", item.Definition);
        Assert.IsTrue(item.HasPendingEdit);
        Assert.IsTrue(item.ShowEditActions);
        item.CancelEdit();
        Assert.AreEqual("# definition", item.DraftDefinition);
    }

    [TestMethod]
    public void NewEntryUsesAnExplicitDraftUntilSaved()
    {
        var item = new DictionaryItem { IsNew = true };
        item.BeginEdit();
        item.DraftWord = "new word";
        item.DraftDefinition = "# new definition";
        Assert.IsTrue(item.IsEditing);
        Assert.IsTrue(item.HasPendingEdit);
        Assert.AreEqual("", item.Word);
        Assert.AreEqual("", item.Definition);

        item.SaveEdit();
        Assert.IsFalse(item.IsEditing);
        Assert.IsFalse(item.IsNew);
        Assert.IsFalse(item.HasPendingEdit);
        Assert.AreEqual("new word", item.Word);
        Assert.AreEqual("# new definition", item.Definition);
    }

    [TestMethod]
    public void OpeningAnotherRowAndReturningPreservesRawMarkdownDraft()
    {
        var item = new DictionaryItem { Word = "word", Definition = "saved" };
        var other = new DictionaryItem { Word = "other" };
        item.BeginEdit();
        item.DraftDefinition = "**unsaved**";
        other.IsExpanded = true;
        other.BeginEdit();
        item.BeginEdit();

        Assert.IsTrue(item.IsEditing);
        Assert.IsTrue(item.HasPendingEdit);
        Assert.AreEqual("**unsaved**", item.DraftDefinition);
        Assert.AreEqual("saved", item.Definition);
        Assert.IsTrue(other.IsExpanded);
    }

    [TestMethod]
    public void UnsavedIndicatorNotifiesWhenDraftChangesAndClearsOnRevert()
    {
        var item = new DictionaryItem { Word = "word", Definition = "saved" };
        var notifications = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DictionaryItem.HasPendingEdit)) notifications++;
        };
        item.BeginEdit();
        Assert.IsFalse(item.HasPendingEdit);
        item.DraftWord = "changed";
        Assert.IsTrue(item.HasPendingEdit);
        item.DraftWord = "word";
        Assert.IsFalse(item.HasPendingEdit);
        item.DraftDefinition = "changed";
        Assert.IsTrue(item.HasPendingEdit);
        item.CancelEdit();
        Assert.IsFalse(item.IsEditing);
        Assert.IsFalse(item.HasPendingEdit);
        Assert.AreEqual("saved", item.DraftDefinition);
        Assert.IsTrue(notifications >= 6);
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
