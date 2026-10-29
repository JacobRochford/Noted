using System.Text.Json;
using System.Text.Json.Serialization;
using Noted.Services;

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
    public bool ShowNotesHeader { get; init; } = false;
    public string? CustomHeader { get; init; }
    public List<string>? PinnedNotes { get; init; }
    [JsonPropertyName("HiddenUtilities")]
    public List<string>? HiddenUtilities { get; init; }

    [JsonPropertyName("HotkeyModifiers")]
    public string NotesHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Notes).Modifiers;

    [JsonPropertyName("HotkeyKey")]
    public string NotesHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Notes).Key;

    [JsonPropertyName("ChecklistHotkeyModifiers")]
    public string ChecklistHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Checklist).Modifiers;
    [JsonPropertyName("ChecklistHotkeyKey")]
    public string ChecklistHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Checklist).Key;
    [JsonPropertyName("DictionaryHotkeyModifiers")]
    public string DictionaryHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Dictionary).Modifiers;
    [JsonPropertyName("DictionaryHotkeyKey")]
    public string DictionaryHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Dictionary).Key;
    [JsonPropertyName("MiniPadHotkeyModifiers")]
    public string MiniPadHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.MiniPad).Modifiers;
    [JsonPropertyName("MiniPadHotkeyKey")]
    public string MiniPadHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.MiniPad).Key;
    [JsonPropertyName("NoteEditorHotkeyModifiers")]
    public string NoteEditorHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.NoteEditor).Modifiers;
    [JsonPropertyName("NoteEditorHotkeyKey")]
    public string NoteEditorHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.NoteEditor).Key;
    [JsonPropertyName("JotCaptureHotkeyModifiers")]
    public string JotCaptureHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.JotCapture).Modifiers;
    [JsonPropertyName("JotCaptureHotkeyKey")]
    public string JotCaptureHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.JotCapture).Key;
    [JsonPropertyName("ScratchpadHotkeyModifiers")]
    public string ScratchpadHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Scratchpad).Modifiers;
    [JsonPropertyName("ScratchpadHotkeyKey")]
    public string ScratchpadHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Scratchpad).Key;
    [JsonPropertyName("JotsHotkeyModifiers")]
    public string JotsHotkeyModifiers { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Jots).Modifiers;
    [JsonPropertyName("JotsHotkeyKey")]
    public string JotsHotkeyKey { get; init; } = HotkeyConfiguration.GetDefaultHotkey(HotkeyFeature.Jots).Key;
    [JsonPropertyName("DisabledHotkeys")]
    public List<string>? DisabledHotkeys { get; init; }
    public int HotkeyDefaultsVersion { get; init; }
    [JsonPropertyName("JotWindows")]
    public Dictionary<Guid, JotsWindowState>? JotWindows { get; init; }
    [JsonPropertyName("AlwaysVisibleWindows")]
    public string[]? AlwaysVisibleWindows { get; init; }
    public bool GhostModeEnabled { get; init; }
    public double GhostModeOpacity { get; init; } = 0.25;
    public double DefaultOpacity { get; init; } = 0.88;
    public AppThemeMode AppThemeMode { get; init; } = AppTheme.DefaultMode;
    public string AccentColor { get; init; } = AppTheme.DefaultAccentColor;
    public FolderNavigationMode FolderNavigationMode { get; init; } = FolderNavigationMode.DrillDown;
    [JsonPropertyName("ChecklistWindowState")]
    public ChecklistWindowState? ChecklistWindowState { get; init; }
    [JsonPropertyName("DictionaryWindowState")]
    public DictionaryWindowState? DictionaryWindowState { get; init; }
    [JsonPropertyName("JotsWindowState")]
    public JotsWindowState? JotsWindowState { get; init; }

    [JsonPropertyName("HideButtonClosesAll")]
    public bool MainHideButtonHidesAll { get; init; } = true;

    public IndentationSettings DefaultIndentation { get; init; } = new();

    public bool NormalizePastedText { get; init; } = true;
    public bool ConfirmNoteDeletion { get; init; } = true;
    public bool IncludeEntireNotesFolderInBackups { get; init; }
    [JsonPropertyName("ScratchpadWindowState")]
    public ScratchpadWindowState? ScratchpadWindowState { get; init; }
    [JsonPropertyName("NoteEditorWindowState")]
    public NoteEditorWindowState? NoteEditorWindowState { get; init; }
    [JsonPropertyName("MiniPadWindowState")]
    public MiniPadWindowState? MiniPadWindowState { get; init; }
    [JsonPropertyName("MiniPadWindows")]
    public Dictionary<Guid, MiniPadWindowState>? MiniPadWindows { get; init; }

    [JsonPropertyName("RestoreEditorSession")]
    public bool ReopenEditorTabsOnStartup { get; init; } = true;

    public string? PreferredDisplayDeviceName { get; init; }
    public bool NotedButtonOnLeft { get; init; }
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
