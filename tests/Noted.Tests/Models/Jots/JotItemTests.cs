using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class JotItemTests
{
    [TestMethod]
    public void SavingBlankTitleDerivesTitleFromFirstMeaningfulDetailLine()
    {
        var item = new JotItem { IsNew = true };
        item.BeginEdit();
        item.DraftDetails = "\n# Kestrel ConfigureKestrel\nMore detail";

        item.SaveEdit(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

        Assert.AreEqual("Kestrel ConfigureKestrel", item.Title);
        Assert.AreEqual("\n# Kestrel ConfigureKestrel\nMore detail", item.Details);
        Assert.IsFalse(item.IsNew);
        Assert.IsFalse(item.IsEditing);
    }

    [TestMethod]
    public void CancelRestoresCommittedValuesWithoutPublishingDraft()
    {
        var item = new JotItem
        {
            Title = "Saved",
            Details = "Saved details",
            Category = "Reference",
            IsPinned = true
        };
        item.BeginEdit();
        item.DraftTitle = "Draft";
        item.DraftDetails = "Draft details";
        item.DraftCategory = "Other";
        item.DraftIsPinned = false;

        item.CancelEdit();

        Assert.AreEqual("Saved", item.Title);
        Assert.AreEqual("Saved details", item.Details);
        Assert.AreEqual("Reference", item.Category);
        Assert.IsTrue(item.IsPinned);
        Assert.AreEqual("Saved", item.DraftTitle);
        Assert.IsFalse(item.IsEditing);
        Assert.IsFalse(item.HasPendingEdit);
    }
}
