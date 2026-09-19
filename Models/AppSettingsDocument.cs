using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noted.Models;

internal sealed record AppSettingsDocument
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long Revision { get; init; }
    public DateTimeOffset? SavedUtc { get; init; }
    public string? NotesDirectory { get; init; }
    public NoteTimestampPlacement TimestampPlacement { get; init; } = NoteTimestampPlacement.None;
    public int TimestampLine { get; init; } = 1;

    [JsonConverter(typeof(NewNoteModeJsonConverter))]
    public NewNoteMode? NewNoteMode { get; init; }

    public bool ShowModifiedSubtitle { get; init; } = true;
    public string? CustomHeader { get; init; }
    public List<string>? PinnedNotes { get; init; }

    [JsonPropertyName("HotkeyModifiers")]
    public string NotesHotkeyModifiers { get; init; } = "Ctrl+Shift";

    [JsonPropertyName("HotkeyKey")]
    public string NotesHotkeyKey { get; init; } = "Space";

    public string ChecklistHotkeyModifiers { get; init; } = "Alt";
    public string ChecklistHotkeyKey { get; init; } = "C";
    public string DictionaryHotkeyModifiers { get; init; } = "Alt";
    public string DictionaryHotkeyKey { get; init; } = "D";
    public string[]? AlwaysVisibleWindows { get; init; }
    public bool GhostModeEnabled { get; init; }
    public double GhostModeOpacity { get; init; } = 0.25;
    public double DefaultOpacity { get; init; } = 0.88;
    public AppThemeMode AppThemeMode { get; init; } = AppThemeMode.System;
    public string AccentColor { get; init; } = AppTheme.DefaultAccentColor;
    public FolderNavigationMode FolderNavigationMode { get; init; } = FolderNavigationMode.DrillDown;
    public ChecklistWindowState? ChecklistWindowState { get; init; }
    public DictionaryWindowState? DictionaryWindowState { get; init; }

    [JsonPropertyName("HideButtonClosesAll")]
    public bool MainHideButtonHidesAll { get; init; } = true;

    public bool ConfirmNoteDeletion { get; init; } = true;
    public ScratchpadWindowState? ScratchpadWindowState { get; init; }
    public NoteEditorWindowState? NoteEditorWindowState { get; init; }
    public MiniPadWindowState? MiniPadWindowState { get; init; }

    [JsonPropertyName("RestoreEditorSession")]
    public bool ReopenEditorTabsOnStartup { get; init; } = true;

    public string? PreferredDisplayDeviceName { get; init; }
}

internal sealed class NewNoteModeJsonConverter : JsonConverter<NewNoteMode?>
{
    public override bool HandleNull => true;

    public override NewNoteMode? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var value) &&
            Enum.IsDefined(typeof(NewNoteMode), value))
        {
            return (NewNoteMode)value;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            using var ignoredValue = JsonDocument.ParseValue(ref reader);
        }

        return NewNoteMode.Prompt;
    }

    public override void Write(
        Utf8JsonWriter writer,
        NewNoteMode? value,
        JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue((int)value.Value);
        else
            writer.WriteNullValue();
    }
}
