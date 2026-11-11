using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class AppSettingsDocumentTests
{
    [TestMethod]
    [DataRow(0, AppThemeMode.System)]
    [DataRow(1, AppThemeMode.Light)]
    [DataRow(2, AppThemeMode.Dark)]
    [DataRow(3, AppThemeMode.Midnight)]
    [DataRow(4, AppThemeMode.OLED)]
    [DataRow(5, AppThemeMode.Warm)]
    public void ThemeIdentifiersRemainCompatible(int id, AppThemeMode mode)
    {
        var document = JsonSerializer.Deserialize<AppSettingsDocument>($"{{\"AppThemeMode\":{id},\"AccentColor\":\"#7A5CB8\"}}");
        Assert.IsNotNull(document);
        Assert.AreEqual(mode, document.AppThemeMode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document));
        Assert.AreEqual(id, json.RootElement.GetProperty("AppThemeMode").GetInt32());
        Assert.AreEqual("#7A5CB8", json.RootElement.GetProperty("AccentColor").GetString());
    }

    [TestMethod]
    public void ExistingSettingsWithoutPastePreferenceDefaultToEnabled()
    {
        var document = JsonSerializer.Deserialize<AppSettingsDocument>("{}");
        Assert.IsNotNull(document);
        Assert.IsTrue(document.NormalizePastedText);
    }

    [TestMethod]
    public void ExistingUtilityBoundsWithoutMaximizedFlagStayNormal()
    {
        var document = JsonSerializer.Deserialize<AppSettingsDocument>("""
            {
              "ChecklistWindowState": { "Width": 380, "Height": 540 },
              "DictionaryWindowState": { "Left": 120, "Top": 80, "Width": 400, "Height": 500 },
              "JotsWindowState": { "Width": 430, "Height": 570 },
              "ScratchpadWindowState": { "Width": 450, "Height": 500 }
            }
            """);

        Assert.IsNotNull(document?.ChecklistWindowState);
        Assert.IsNotNull(document.DictionaryWindowState);
        Assert.IsNotNull(document.JotsWindowState);
        Assert.IsNotNull(document.ScratchpadWindowState);
        Assert.IsFalse(document.ChecklistWindowState.IsMaximized);
        Assert.IsFalse(document.DictionaryWindowState.IsMaximized);
        Assert.IsFalse(document.JotsWindowState.IsMaximized);
        Assert.IsFalse(document.ScratchpadWindowState.IsMaximized);
        Assert.AreEqual(400, document.DictionaryWindowState.Width);
        Assert.AreEqual(500, document.DictionaryWindowState.Height);
    }

    [TestMethod]
    public void SerializationKeepsEstablishedSettingsPropertyNames()
    {
        var document = new AppSettingsDocument
        {
            NotesHotkeyModifiers = "Alt",
            NotesHotkeyKey = "N",
            ReopenEditorTabsOnStartup = false
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document));
        var root = json.RootElement;

        Assert.AreEqual("Alt", root.GetProperty("HotkeyModifiers").GetString());
        Assert.AreEqual("N", root.GetProperty("HotkeyKey").GetString());
        Assert.IsFalse(root.GetProperty("RestoreEditorSession").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("NotesHotkeyModifiers", out _));
        Assert.IsFalse(root.TryGetProperty("ReopenEditorTabsOnStartup", out _));
    }

    [TestMethod]
    public void InvalidNewNoteModeFallsBackToPrompt()
    {
        var document = JsonSerializer.Deserialize<AppSettingsDocument>("{\"NewNoteMode\":999}");

        Assert.IsNotNull(document);
        Assert.AreEqual(NewNoteMode.Prompt, document.NewNoteMode);
    }
}
