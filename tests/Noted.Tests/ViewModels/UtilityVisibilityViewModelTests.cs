using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.Tests.Services;
using Noted.ViewModels;

namespace Noted.Tests.ViewModels;

[TestClass]
public sealed class UtilityVisibilityViewModelTests
{
    [TestMethod]
    public void NewSettingsShowAllUtilitiesByDefault()
    {
        using var directory = new TemporaryTestDirectory();
        AssertAllVisible(CreateModel(directory.Path));
        Assert.IsFalse(File.Exists(directory.File("settings.json")));
    }

    [TestMethod]
    [DataRow("{\"CustomHeader\":\"My Notes\"}")]
    [DataRow("{\"SchemaVersion\":1,\"CustomHeader\":\"My Notes\"}")]
    [DataRow("{\"SchemaVersion\":1,\"CustomHeader\":\"My Notes\",\"HiddenUtilities\":null}")]
    public void ExistingSettingsKeepAllUtilitiesVisibleWithoutRewritingOnLoad(string json)
    {
        using var directory = new TemporaryTestDirectory();
        File.WriteAllText(directory.File("settings.json"), json);
        var original = File.ReadAllBytes(directory.File("settings.json"));
        var model = CreateModel(directory.Path);
        AssertAllVisible(model);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(directory.File("settings.json")));
        model.Utilities[0].IsVisible = false;
        Assert.AreEqual("My Notes", new AppSettingsService(directory.Path).LoadCustomHeader());
    }

    [TestMethod]
    public void HidingAndReenablingPersistsAndPreservesUtilityStateAndContent()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var checklist = new ChecklistWindowState { Left = 42, SelectedTabId = "saved-tab", ReopenOnStartup = true };
        var dictionary = new DictionaryWindowState { Left = 43, ReopenOnStartup = true };
        var jots = new JotsWindowState { Left = 43.5, ReopenOnStartup = true };
        var scratchpad = new ScratchpadWindowState { Left = 44, ReopenOnStartup = true };
        var miniPad = new MiniPadWindowState { WordWrapEnabled = false, ReopenOnStartup = true };
        var editor = new NoteEditorWindowState { ReopenOnStartup = true, Width = 950 };
        settings.SaveChecklistWindowState(checklist);
        settings.SaveDictionaryWindowState(dictionary);
        settings.SaveJotsWindowState(jots);
        settings.SaveScratchpadWindowState(scratchpad);
        settings.SaveMiniPadWindowState(miniPad);
        settings.SaveNoteEditorWindowState(editor);
        settings.SaveReopenEditorTabsOnStartup(false);
        settings.SaveWindowAlwaysVisible("Checklist", true);
        var contentPath = directory.File("checklist.json");
        File.WriteAllText(contentPath, "[{\"Text\":\"Keep this item\"}]");
        var content = File.ReadAllBytes(contentPath);
        var dictionaryPath = directory.File("dictionary.json");
        File.WriteAllText(dictionaryPath, "[{\"Word\":\"Keep this word\",\"Description\":\"Keep this description\"}]");
        var dictionaryContent = File.ReadAllBytes(dictionaryPath);
        var scratchpadPath = directory.File("scratchpad.rtf");
        File.WriteAllText(scratchpadPath, "{\\rtf1 Keep this scratchpad}");
        var scratchpadContent = File.ReadAllBytes(scratchpadPath);
        var model = CreateModel(directory.Path);
        foreach (var option in model.Utilities) option.IsVisible = false;
        Assert.IsFalse(model.HasVisibleUtilities);
        Assert.AreEqual(0, model.VisibleUtilities.Count);

        var reloaded = CreateModel(directory.Path);
        Assert.IsTrue(reloaded.Utilities.All(option => !option.IsVisible));
        reloaded.Utilities[1].IsVisible = true;
        CollectionAssert.AreEqual(new[] { "Dictionary" }, reloaded.VisibleUtilities.Select(option => option.Id).ToArray());
        foreach (var option in reloaded.Utilities) option.IsVisible = true;
        AssertAllVisible(CreateModel(directory.Path));

        var persisted = new AppSettingsService(directory.Path);
        Assert.AreEqual(checklist, persisted.LoadChecklistWindowState());
        Assert.AreEqual(dictionary, persisted.LoadDictionaryWindowState());
        Assert.AreEqual(jots, persisted.LoadJotsWindowState());
        Assert.AreEqual(scratchpad, persisted.LoadScratchpadWindowState());
        Assert.AreEqual(miniPad, persisted.LoadMiniPadWindowState());
        Assert.AreEqual(editor, persisted.LoadNoteEditorWindowState());
        Assert.IsFalse(persisted.LoadReopenEditorTabsOnStartup());
        Assert.IsTrue(persisted.LoadWindowAlwaysVisible("Checklist"));
        Assert.AreEqual(("Alt", "C"), persisted.LoadChecklistHotkey());
        Assert.AreEqual(("Alt", "D"), persisted.LoadDictionaryHotkey());
        Assert.AreEqual(("Alt", "M"), persisted.LoadMiniPadHotkey());
        CollectionAssert.AreEqual(content, File.ReadAllBytes(contentPath));
        CollectionAssert.AreEqual(dictionaryContent, File.ReadAllBytes(dictionaryPath));
        CollectionAssert.AreEqual(scratchpadContent, File.ReadAllBytes(scratchpadPath));
    }

    [TestMethod]
    public void FailedSaveLeavesVisibilityAndFilteringUnchanged()
    {
        using var directory = new TemporaryTestDirectory();
        var model = new UtilityVisibilityViewModel(new AppSettingsService(directory.Path), _ => false);
        model.Utilities[0].IsVisible = false;
        AssertAllVisible(model);
        Assert.AreEqual(0, new AppSettingsService(directory.Path).LoadHiddenUtilities().Count);
    }

    private static UtilityVisibilityViewModel CreateModel(string directory) =>
        new(new AppSettingsService(directory), save => { save(); return true; });

    private static void AssertAllVisible(UtilityVisibilityViewModel model)
    {
        CollectionAssert.AreEqual(new[] { "Checklist", "Dictionary", "Jots", "Scratchpad", "MiniPad" },
            model.VisibleUtilities.Select(option => option.Id).ToArray());
        Assert.IsTrue(model.HasVisibleUtilities);
        Assert.IsTrue(model.Utilities.All(option => option.IsVisible));
    }
}
