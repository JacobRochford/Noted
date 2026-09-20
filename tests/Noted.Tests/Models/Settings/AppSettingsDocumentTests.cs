using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class AppSettingsDocumentTests
{
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
