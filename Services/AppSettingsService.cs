using System.IO;
using System.Text.Json;
namespace Noted.Services;

/// Specifies where a timestamp should be placed in newly created notes.
public enum NoteTimestampPlacement {
    None,
    Top,
    Bottom
}

/// Manages application settings stored in JSON format in the local app data folder.
public sealed class AppSettingsService {
    private readonly string _settingsFilePath;
    private AppSettings? _cachedSettings;

    /// Gets the directory where settings are stored.
    public string StorageDirectory { get; }

    public AppSettingsService() {
        StorageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Noted");
        Directory.CreateDirectory(StorageDirectory);
        _settingsFilePath = Path.Combine(StorageDirectory, "settings.json");
    }

    /// Loads the configured notes directory path.
    public string? LoadNotesDirectory() {
        var dir = LoadSetting(s => s.NotesDirectory);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    /// Saves the notes directory path to settings.
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

    // Loads whether to prompt for note name when creating new notes depending on user settings.
    public bool LoadPromptForNoteName() {
        return LoadSetting(s => s.PromptForNoteName);
    }

    /// Saves whether to prompt for note name when creating new notes depending on user settings.
    public void SavePromptForNoteName(bool promptForNoteName) {
        SaveSetting(s => s with { PromptForNoteName = promptForNoteName });
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

    public bool LoadGhostModeEnabled() => LoadSetting(s => s.GhostModeEnabled);
    public void SaveGhostModeEnabled(bool enabled) => SaveSetting(s => s with { GhostModeEnabled = enabled });
    public double LoadGhostModeOpacity() => LoadSetting(s => s.GhostModeOpacity);
    public void SaveGhostModeOpacity(double opacity) => SaveSetting(s => s with { GhostModeOpacity = opacity });
    public double LoadDefaultOpacity() => LoadSetting(s => s.DefaultOpacity);
    public void SaveDefaultOpacity(double opacity) => SaveSetting(s => s with { DefaultOpacity = opacity });

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
            _cachedSettings = new AppSettings();
            return _cachedSettings;
        }

        try {
            var json = File.ReadAllText(_settingsFilePath);
            _cachedSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            return _cachedSettings;
        } catch (JsonException) {
            // Settings file is corrupted; return defaults without caching so
            // a subsequent save can overwrite the bad file cleanly.
            return new AppSettings();
        }
    }

    private void SaveSettings(AppSettings settings) {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        // Write to .tmp then rename; File.Move on the same drive is atomic,
        // so a crash mid-write can't leave settings.json half-baked or missing.
        var tmpPath = _settingsFilePath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _settingsFilePath, overwrite: true);
        _cachedSettings = settings;
    }

    /// Represents the application settings stored in JSON.
    private sealed record AppSettings {
        public string? NotesDirectory { get; init; }
        public NoteTimestampPlacement TimestampPlacement { get; init; } = NoteTimestampPlacement.None;
        public bool PromptForNoteName { get; init; }
        public bool ShowModifiedSubtitle { get; init; } = true;
        public string? CustomHeader { get; init; }
        public List<string>? PinnedNotes { get; init; }
        public string HotkeyModifiers { get; init; } = "Ctrl+Shift";
        public string HotkeyKey { get; init; } = "Space";
        public bool GhostModeEnabled { get; init; } = false;
        public double GhostModeOpacity { get; init; } = 0.25;
        public double DefaultOpacity { get; init; } = 0.88;
    }
}