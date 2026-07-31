using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noted.Models;

namespace Noted.Services;

internal sealed class NewNoteModeJsonConverter : JsonConverter<NewNoteMode?> {
    public override bool HandleNull => true;

    public override NewNoteMode? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var value)
                && Enum.IsDefined(typeof(NewNoteMode), value)) {
            return (NewNoteMode)value;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) {
            using var ignoredValue = JsonDocument.ParseValue(ref reader);
        }

        return NewNoteMode.Prompt;
    }

    public override void Write(
            Utf8JsonWriter writer,
            NewNoteMode? value,
            JsonSerializerOptions options) {
        if (value.HasValue) {
            writer.WriteNumberValue((int)value.Value);
        } else {
            writer.WriteNullValue();
        }
    }
}

/// Manages application settings stored in JSON format in the local app data folder.
public sealed class AppSettingsService : IAppSettingsService {
    private const int MaxSettingsBackups = 50;
    private readonly string _settingsFilePath;
    private AppSettings? _cachedSettings;

    public string AppDataDirectory { get; }

    public AppSettingsService(string? appDataDirectory = null) {
        AppDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Noted")
            : Path.GetFullPath(appDataDirectory);
        _settingsFilePath = Path.Combine(AppDataDirectory, "settings.json");

        try {
            Directory.CreateDirectory(AppDataDirectory);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            throw CreatePersistenceException(
                $"Noted could not initialize its application data folder at '{AppDataDirectory}'. "
                + "Check that the location exists and that you have permission to write to it, then restart Noted.",
                ex);
        }
    }

    public string? LoadNotesDirectory() {
        var dir = LoadSetting(s => s.NotesDirectory);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    public void SaveNotesDirectory(string notesDirectory) {
        SaveSetting(s => s with { NotesDirectory = notesDirectory });
    }

    public IReadOnlyList<string> LoadPinnedNotes() {
        return LoadSetting(s => s.PinnedNotes ?? new List<string>());
    }

    public void SavePinnedNotes(IReadOnlyList<string> pinnedNotes) {
        SaveSetting(s => s with { PinnedNotes = pinnedNotes?.ToList() ?? new List<string>() });
    }

    public bool LoadShowModifiedSubtitle() {
        return LoadSetting(s => s.ShowModifiedSubtitle);
    }

    public void SaveShowModifiedSubtitle(bool showModifiedSubtitle) {
        SaveSetting(s => s with { ShowModifiedSubtitle = showModifiedSubtitle });
    }

    public NoteTimestampPlacement LoadTimestampPlacement() {
        return LoadSetting(s => s.TimestampPlacement);
    }

    public void SaveTimestampPlacement(NoteTimestampPlacement timestampPlacement) {
        SaveSetting(s => s with { TimestampPlacement = timestampPlacement });
    }

    public NewNoteMode LoadNewNoteMode() {
        return LoadSetting(s => s.NewNoteMode ?? NewNoteMode.Prompt);
    }

    public void SaveNewNoteMode(NewNoteMode newNoteMode) {
        if (!Enum.IsDefined(typeof(NewNoteMode), newNoteMode)) {
            throw new ArgumentOutOfRangeException(
                nameof(newNoteMode), newNoteMode, "Undefined note creation mode.");
        }

        SaveSetting(s => s with { NewNoteMode = newNoteMode });
    }

    public bool LoadPromptForNoteName() {
        return LoadNewNoteMode() == NewNoteMode.Prompt;
    }

    public void SavePromptForNoteName(bool promptForNoteName) {
        SaveNewNoteMode(promptForNoteName ? NewNoteMode.Prompt : NewNoteMode.Quick);
    }

    public string? LoadCustomHeader() {
        return LoadSetting(s => s.CustomHeader);
    }

    public void SaveCustomHeader(string? customHeader) {
        SaveSetting(s => s with { CustomHeader = customHeader });
    }

    public (string modifiers, string key) LoadGlobalHotkey()
    {
        var setting = LoadSettings();
        return (setting.HotkeyModifiers ?? "Ctrl+Shift", setting.HotkeyKey ?? "Space");
    }

    public void SaveGlobalHotkey(string modifiers, string key)
    {
        SaveSetting(s => s with { HotkeyModifiers = modifiers, HotkeyKey = key });
    }

    public (string modifiers, string key) LoadChecklistHotkey()
    {
        var setting = LoadSettings();
        return (setting.ChecklistHotkeyModifiers ?? "Alt", setting.ChecklistHotkeyKey ?? "C");
    }

    public void SaveChecklistHotkey(string modifiers, string key)
    {
        SaveSetting(s => s with { ChecklistHotkeyModifiers = modifiers, ChecklistHotkeyKey = key });
    }

    public (string modifiers, string key) LoadDictionaryHotkey()
    {
        var setting = LoadSettings();
        return (setting.DictionaryHotkeyModifiers ?? "Alt", setting.DictionaryHotkeyKey ?? "D");
    }

    public void SaveDictionaryHotkey(string modifiers, string key)
    {
        SaveSetting(s => s with { DictionaryHotkeyModifiers = modifiers, DictionaryHotkeyKey = key });
    }

    public bool LoadGhostModeEnabled() => LoadSetting(s => s.GhostModeEnabled);
    public void SaveGhostModeEnabled(bool enabled) => SaveSetting(s => s with { GhostModeEnabled = enabled });
    public double LoadGhostModeOpacity() => LoadSetting(s => s.GhostModeOpacity);
    public void SaveGhostModeOpacity(double opacity) => SaveSetting(s => s with { GhostModeOpacity = opacity });
    public double LoadDefaultOpacity() => LoadSetting(s => s.DefaultOpacity);
    public void SaveDefaultOpacity(double opacity) => SaveSetting(s => s with { DefaultOpacity = opacity });

    public FolderNavigationMode LoadFolderNavigationMode() => LoadSetting(s => s.FolderNavigationMode);
    public void SaveFolderNavigationMode(FolderNavigationMode mode) => SaveSetting(s => s with { FolderNavigationMode = mode });

    public IReadOnlyList<ChecklistItemState> LoadChecklistItems()
    {
        return LoadSetting(s => s.ChecklistItems ?? new List<ChecklistItemState>());
    }

    public void SaveChecklistItems(IReadOnlyList<ChecklistItemState> items)
    {
        SaveSetting(s => s with { ChecklistItems = items?.ToList() ?? new List<ChecklistItemState>() });
    }

    public ChecklistWindowState LoadChecklistWindowState()
    {
        return LoadSetting(s => s.ChecklistWindowState ?? new ChecklistWindowState());
    }

    public void SaveChecklistWindowState(ChecklistWindowState state)
    {
        SaveSetting(s => s with { ChecklistWindowState = state });
    }

    public IReadOnlyList<DictionaryItemState> LoadDictionaryItems()
    {
        return LoadSetting(s => s.DictionaryItems ?? new List<DictionaryItemState>());
    }

    public void SaveDictionaryItems(IReadOnlyList<DictionaryItemState> items)
    {
        SaveSetting(s => s with { DictionaryItems = items?.ToList() ?? new List<DictionaryItemState>() });
    }

    public DictionaryWindowState LoadDictionaryWindowState()
    {
        return LoadSetting(s => s.DictionaryWindowState ?? new DictionaryWindowState());
    }

    public void SaveDictionaryWindowState(DictionaryWindowState state)
    {
        SaveSetting(s => s with { DictionaryWindowState = state });
    }

    public bool LoadHideButtonHidesAll()
    {
        return LoadSetting(s => s.HideButtonHidesAll);
    }

    public void SaveHideButtonHidesAll(bool hidesAll)
    {
        SaveSetting(s => s with { HideButtonHidesAll = hidesAll });
    }

    public bool LoadConfirmNoteDeletion()
    {
        return LoadSetting(s => s.ConfirmNoteDeletion);
    }

    public void SaveConfirmNoteDeletion(bool confirm)
    {
        SaveSetting(s => s with { ConfirmNoteDeletion = confirm });
    }

    public ScratchpadWindowState LoadScratchpadWindowState()
    {
        return LoadSetting(s => s.ScratchpadWindowState ?? new ScratchpadWindowState());
    }

    public void SaveScratchpadWindowState(ScratchpadWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SaveSetting(s => s with { ScratchpadWindowState = state });
    }

    public NoteEditorWindowState LoadNoteEditorWindowState()
    {
        return LoadSetting(s => s.NoteEditorWindowState ?? new NoteEditorWindowState());
    }

    public void SaveNoteEditorWindowState(NoteEditorWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SaveSetting(s => s with { NoteEditorWindowState = state });
    }

    public bool LoadRestoreEditorSession()
    {
        return LoadSetting(s => s.RestoreEditorSession);
    }

    public void SaveRestoreEditorSession(bool enabled)
    {
        SaveSetting(s => s with { RestoreEditorSession = enabled });
    }

    public string? LoadPreferredDisplayDeviceName()
    {
        return LoadSetting(s => s.PreferredDisplayDeviceName);
    }

    public void SavePreferredDisplayDeviceName(string? deviceName)
    {
        SaveSetting(s => s with
        {
            PreferredDisplayDeviceName = string.IsNullOrWhiteSpace(deviceName)
                ? null
                : deviceName
        });
    }

    private T LoadSetting<T>(Func<AppSettings, T> selector) {
        return selector(LoadSettings());
    }

    private void SaveSetting(Func<AppSettings, AppSettings> updater) {
        var settings = updater(LoadSettings());
        SaveSettings(settings);
    }

    private AppSettings LoadSettings() {
        if (_cachedSettings is not null)
            return _cachedSettings;

        if (!File.Exists(_settingsFilePath)) {
            _cachedSettings = NormalizeSettings(new AppSettings());
            return _cachedSettings;
        }

        try {
            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            _cachedSettings = NormalizeSettings(settings);
            return _cachedSettings;
        } catch (JsonException ex) {
            throw CreatePersistenceException(
                $"Noted could not read settings from '{_settingsFilePath}' because the file is malformed. "
                + "Move or repair settings.json, then restart Noted. The existing file was not changed.",
                ex);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            throw CreatePersistenceException(
                $"Noted could not read settings from '{_settingsFilePath}'. "
                + "Check that the file is available and that you have permission to read it, then retry.",
                ex);
        }
    }

    private static AppSettings NormalizeSettings(AppSettings settings) {
        var mode = settings.NewNoteMode
            ?? (settings.PromptForNoteName.HasValue
                ? settings.PromptForNoteName.Value
                    ? NewNoteMode.Prompt
                    : NewNoteMode.Quick
                : NewNoteMode.Prompt);

        return settings with {
            NewNoteMode = mode,
            PromptForNoteName = null
        };
    }

    private void SaveSettings(AppSettings settings) {
        var tmpPath = _settingsFilePath + ".tmp";

        try {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);

            var backupDir = Path.Combine(
                Path.GetDirectoryName(_settingsFilePath)!,
                "backups"
            );

            Directory.CreateDirectory(backupDir);

            if (File.Exists(_settingsFilePath)) {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                var fileName = Path.GetFileNameWithoutExtension(_settingsFilePath);
                var ext = Path.GetExtension(_settingsFilePath);
                var backupPath = Path.Combine(backupDir, $"{fileName}_{timestamp}{ext}");
                var suffix = 1;

                while (File.Exists(backupPath)) {
                    backupPath = Path.Combine(backupDir, $"{fileName}_{timestamp}_{suffix:00}{ext}");
                    suffix++;
                }

                File.Copy(_settingsFilePath, backupPath, overwrite: false);
            }

            // Write to .tmp then rename; File.Move on the same drive is atomic,
            // so a crash mid-write can't leave settings.json half-baked or missing.
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _settingsFilePath, overwrite: true);
            _cachedSettings = settings;
            PruneSettingsBackups(backupDir);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            TryDeleteTemporarySettingsFile(tmpPath);
            throw CreatePersistenceException(
                $"Noted could not save settings to '{_settingsFilePath}'. "
                + "Your latest setting change was not saved. Check that the location is available and writable, then retry.",
                ex);
        }
    }

    private static bool IsExpectedSettingsIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private static SettingsPersistenceException CreatePersistenceException(string message, Exception innerException) =>
        new(message, innerException);

    private static void TryDeleteTemporarySettingsFile(string path) {
        try {
            if (File.Exists(path))
                File.Delete(path);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            // Preserve the original persistence error. A later successful save
            // replaces this same temporary path.
        }
    }

    private static void PruneSettingsBackups(string backupDir) {
        try {
            var backups = Directory.GetFiles(backupDir, "settings_*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Skip(MaxSettingsBackups);

            foreach (var backup in backups) {
                try { backup.Delete(); } catch { }
            }
        } catch { }
    }

    /// Represents the application settings stored in JSON.
    private sealed record AppSettings {
        public string? NotesDirectory { get; init; }
        public NoteTimestampPlacement TimestampPlacement { get; init; } = NoteTimestampPlacement.None;
        [JsonConverter(typeof(NewNoteModeJsonConverter))]
        public NewNoteMode? NewNoteMode { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? PromptForNoteName { get; init; }
        public bool ShowModifiedSubtitle { get; init; } = true;
        public string? CustomHeader { get; init; }
        public List<string>? PinnedNotes { get; init; }
        public string HotkeyModifiers { get; init; } = "Ctrl+Shift";
        public string HotkeyKey { get; init; } = "Space";
        public string ChecklistHotkeyModifiers { get; init; } = "Alt";
        public string ChecklistHotkeyKey { get; init; } = "C";
        public string DictionaryHotkeyModifiers { get; init; } = "Alt";
        public string DictionaryHotkeyKey { get; init; } = "D";
        public bool GhostModeEnabled { get; init; } = false;
        public double GhostModeOpacity { get; init; } = 0.25;
        public double DefaultOpacity { get; init; } = 0.88;
        public FolderNavigationMode FolderNavigationMode { get; init; } = FolderNavigationMode.DrillDown;
        public List<ChecklistItemState>? ChecklistItems { get; init; }
        public ChecklistWindowState? ChecklistWindowState { get; init; }
        public List<DictionaryItemState>? DictionaryItems { get; init; }
        public DictionaryWindowState? DictionaryWindowState { get; init; }
        [JsonPropertyName("HideButtonClosesAll")]
        public bool HideButtonHidesAll { get; init; } = true;
        public bool ConfirmNoteDeletion { get; init; } = true;
        public ScratchpadWindowState? ScratchpadWindowState { get; init; }
        public NoteEditorWindowState? NoteEditorWindowState { get; init; }
        public bool RestoreEditorSession { get; init; } = true;
        public string? PreferredDisplayDeviceName { get; init; }
    }
}
