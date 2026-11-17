using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class TasksNamingTests
{
    [TestMethod]
    public void TasksLabelKeepsHistoricalUtilityAndHotkeyIdentities()
    {
        var utility = UtilityCatalog.Utilities.Single(item => item.Id == "Checklist");
        Assert.AreEqual("Tasks", utility.DisplayName);
        Assert.AreEqual("Checklist", PersistenceKeys.Checklist);
        Assert.AreEqual("ChecklistWindow", PersistenceKeys.ChecklistWindow);
        Assert.AreEqual("Tasks", HotkeyConfiguration.GetHotkeyFeatureName(HotkeyFeature.Checklist));
        Assert.AreEqual(("Alt", "C"), HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Checklist));
    }
}
