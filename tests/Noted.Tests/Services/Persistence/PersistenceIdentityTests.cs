using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class PersistenceIdentityTests
{
    [TestMethod]
    [DataRow(HotkeyFeature.Notes, "Notes")]
    [DataRow(HotkeyFeature.Checklist, "Checklist")]
    [DataRow(HotkeyFeature.Dictionary, "Dictionary")]
    [DataRow(HotkeyFeature.MiniPad, "MiniPad")]
    [DataRow(HotkeyFeature.NoteEditor, "NoteEditor")]
    [DataRow(HotkeyFeature.JotCapture, "JotCapture")]
    [DataRow(HotkeyFeature.Scratchpad, "Scratchpad")]
    [DataRow(HotkeyFeature.Jots, "Jots")]
    public void HistoricalHotkeyIdentitiesRoundTrip(object featureValue, string historicalKey)
    {
        var feature = (HotkeyFeature)featureValue;
        Assert.AreEqual(historicalKey, PersistenceKeys.GetHotkey(feature));
        using var directory = new TemporaryTestDirectory();
        File.WriteAllText(directory.File("settings.json"), """
            {"HotkeyDefaultsVersion":1,"DisabledHotkeys":["Notes","Checklist","Dictionary","MiniPad",
            "NoteEditor","JotCapture","Scratchpad","Jots","FutureFeature","minipad"]}
            """);
        var settings = new AppSettingsService(directory.Path);
        Assert.IsFalse(settings.LoadHotkeyEnabled(historicalKey));
        settings.SaveGlobalHotkey(PersistenceKeys.GetHotkey(feature), "Ctrl+Alt", "Q", true);
        Assert.IsTrue(new AppSettingsService(directory.Path).LoadHotkeyEnabled(historicalKey));
        settings.SaveGlobalHotkey(PersistenceKeys.GetHotkey(feature), "Ctrl+Alt", "Q", false);
        using var saved = JsonDocument.Parse(File.ReadAllText(directory.File("settings.json")));
        var disabled = saved.RootElement.GetProperty("DisabledHotkeys").EnumerateArray().Select(x => x.GetString()).ToArray();
        CollectionAssert.Contains(disabled, historicalKey);
        CollectionAssert.Contains(disabled, "FutureFeature");
        CollectionAssert.Contains(disabled, "minipad");
    }

    [TestMethod]
    [DataRow("3")]
    [DataRow(" MiniPad ")]
    [DataRow("Notes, MiniPad")]
    public void LegacyHotkeyStringInputsRetainRawIdentity(string value)
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveGlobalHotkey(value, "Ctrl+Alt", "Q", false);
        Assert.IsFalse(new AppSettingsService(directory.Path).LoadHotkeyEnabled(value));
        Assert.IsTrue(settings.LoadHotkeyEnabled("MiniPad"));
        Assert.AreEqual(("Ctrl+Alt", "Q"), settings.LoadMiniPadHotkey());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => settings.SaveGlobalHotkey("minipad", "Alt", "M", false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => settings.SaveGlobalHotkey("999", "Alt", "M", false));
    }

    [TestMethod]
    [DataRow(typeof(MainWindow), "MainWindow")]
    [DataRow(typeof(ChecklistWindow), "ChecklistWindow")]
    [DataRow(typeof(DictionaryWindow), "DictionaryWindow")]
    [DataRow(typeof(MiniPadWindow), "MiniPadWindow")]
    [DataRow(typeof(NoteEditorWindow), "NoteEditorWindow")]
    [DataRow(typeof(JotCaptureWindow), "JotCaptureWindow")]
    [DataRow(typeof(ScratchpadWindow), "ScratchpadWindow")]
    [DataRow(typeof(JotsWindow), "JotsWindow")]
    [DataRow(typeof(AccentColorDialog), "AccentColorDialog")]
    [DataRow(typeof(AppDialog), "AppDialog")]
    [DataRow(typeof(BackupPreviewDialog), "BackupPreviewDialog")]
    [DataRow(typeof(ChecklistPriorityColorsDialog), "ChecklistPriorityColorsDialog")]
    [DataRow(typeof(NewNoteNameDialog), "NewNoteNameDialog")]
    [DataRow(typeof(System.Windows.Window), "Window")]
    public void WindowAppearanceRetainsHistoricalKey(Type type, string key)
    {
        Assert.AreEqual(key, WindowAppearance.GetTypeSettingsKey(type));
        using var directory = new TemporaryTestDirectory();
        File.WriteAllText(directory.File("settings.json"), "{\"AlwaysVisibleWindows\":[\"" + key + "\"]}");
        var settings = new AppSettingsService(directory.Path);
        Assert.IsTrue(settings.LoadWindowAlwaysVisible(WindowAppearance.GetTypeSettingsKey(type)));
        settings.SaveWindowAlwaysVisible(WindowAppearance.GetTypeSettingsKey(type), true);
        using var saved = JsonDocument.Parse(File.ReadAllText(directory.File("settings.json")));
        Assert.AreEqual(key, saved.RootElement.GetProperty("AlwaysVisibleWindows")[0].GetString());
    }

    [TestMethod]
    public void ChangedUtilityLabelDoesNotChangeHiddenIdentity()
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var model = new UtilityVisibilityViewModel(settings, save => { save(); return true; });
        var definition = UtilityCatalog.Utilities.Single(x => x.Id == "MiniPad") with { DisplayName = "Renamed label" };
        var option = new UtilityOption(definition, model) { IsVisible = false };
        CollectionAssert.AreEqual(new[] { "MiniPad" }, settings.LoadHiddenUtilities().ToArray());
        Assert.AreEqual("Renamed label", option.DisplayName);
        Assert.AreEqual("JotCapture", PersistenceKeys.GetHotkey(HotkeyFeature.JotCapture));
        Assert.AreEqual("Quick Jot", HotkeyConfiguration.GetHotkeyFeatureName(HotkeyFeature.JotCapture));
    }

    [TestMethod]
    public void HistoricalSettingsPropertiesAndEnumRepresentationsRoundTrip()
    {
        const string historical = """
            {"HotkeyModifiers":"Alt","HotkeyKey":"N","ChecklistHotkeyModifiers":"Alt","ChecklistHotkeyKey":"C",
            "DictionaryHotkeyModifiers":"Alt","DictionaryHotkeyKey":"D","MiniPadHotkeyModifiers":"Alt","MiniPadHotkeyKey":"M",
            "NoteEditorHotkeyModifiers":"Alt","NoteEditorHotkeyKey":"E","JotCaptureHotkeyModifiers":"Alt","JotCaptureHotkeyKey":"J",
            "ScratchpadHotkeyModifiers":"Alt","ScratchpadHotkeyKey":"S","JotsHotkeyModifiers":"Alt\u002BShift","JotsHotkeyKey":"J",
            "DisabledHotkeys":["MiniPad"],"HiddenUtilities":["Dictionary"],"AlwaysVisibleWindows":["MiniPadWindow"],
            "ChecklistWindowState":null,"DictionaryWindowState":null,"JotsWindowState":null,"JotWindows":null,
            "ScratchpadWindowState":null,"NoteEditorWindowState":null,"MiniPadWindowState":null,"MiniPadWindows":null,
            "HideButtonClosesAll":false,"RestoreEditorSession":false,"AppThemeMode":2,"TimestampPlacement":3,
            "FolderNavigationMode":1,"NewNoteMode":0}
            """;
        var settings = JsonSerializer.Deserialize<AppSettingsDocument>(historical)!;
        using var original = JsonDocument.Parse(historical);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        foreach (var property in original.RootElement.EnumerateObject())
            Assert.AreEqual(property.Value.GetRawText(), saved.RootElement.GetProperty(property.Name).GetRawText(), property.Name);
        Assert.IsFalse(saved.RootElement.TryGetProperty("NotesHotkeyKey", out _));
        Assert.IsFalse(saved.RootElement.TryGetProperty("MainHideButtonHidesAll", out _));
    }
}
