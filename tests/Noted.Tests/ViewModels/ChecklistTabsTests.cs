using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class ChecklistTabsTests
{
    [TestMethod]
    public void FailedSavesRollBackTabMutations()
    {
        var tabs = new ChecklistTabs(
            [CustomTab("work", "Work", 100)],
            _ => false);
        var work = tabs.Find("work")!;

        Assert.IsFalse(tabs.SetDefaultVisibility(ChecklistTab.AllId, false));
        Assert.IsTrue(tabs.Find(ChecklistTab.AllId)!.IsVisible);
        Assert.IsFalse(tabs.TryCreate("Personal", out var created));
        Assert.IsNull(created);
        Assert.IsFalse(tabs.TryRename(work, "Renamed"));
        Assert.AreEqual("Work", work.Name);
        Assert.IsFalse(tabs.TryDelete(work));
        Assert.IsTrue(tabs.All.Contains(work));
    }

    [TestMethod]
    public void DeletingTheSelectedTabSelectsTheFirstRemainingVisibleTab()
    {
        var savedSnapshots = new List<IReadOnlyList<ChecklistTabState>>();
        var tabs = new ChecklistTabs(
            [
                new ChecklistTabState
                {
                    Id = ChecklistTab.AllId,
                    Kind = ChecklistTabKind.All,
                    IsVisible = false
                },
                CustomTab("work", "Work", 100)
            ],
            snapshot =>
            {
                savedSnapshots.Add(snapshot);
                return true;
            });
        var work = tabs.Find("work")!;
        tabs.Select(work);
        var selectionChanges = 0;
        tabs.SelectionChanged += () => selectionChanges++;

        Assert.IsTrue(tabs.TryDelete(work));

        Assert.AreEqual(ChecklistTab.UrgentId, tabs.Selected!.Id);
        Assert.AreEqual(1, selectionChanges);
        Assert.HasCount(1, savedSnapshots);
        Assert.IsFalse(savedSnapshots[0].Any(tab => tab.Id == "work"));
    }

    [TestMethod]
    public void InitializationRepairsDuplicateCustomIdsAndNames()
    {
        var tabs = new ChecklistTabs(
            [
                CustomTab("duplicate", "Work", 100),
                CustomTab("duplicate", "Work", 101)
            ],
            _ => true);
        var customTabs = tabs.Custom.ToList();

        Assert.HasCount(2, customTabs);
        Assert.IsFalse(string.Equals(
            customTabs[0].Id,
            customTabs[1].Id,
            StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("Work", customTabs[0].Name);
        Assert.AreEqual("Work (2)", customTabs[1].Name);
    }

    private static ChecklistTabState CustomTab(string id, string name, int order) =>
        new()
        {
            Id = id,
            Name = name,
            Kind = ChecklistTabKind.Custom,
            IsVisible = true,
            Order = order
        };
}
