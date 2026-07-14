using System.IO;
using System.Text.Json;
using Noted.Models;

namespace Noted.Services;

public sealed record ChecklistItemData
{
    public string? Text { get; init; }
    public bool IsChecked { get; init; }
    public ChecklistPriority Priority { get; init; } = ChecklistPriority.None;
    public DateTime? DueDate { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

public sealed record ChecklistWindowState
{
    public double Left { get; init; } = 100;
    public double Top { get; init; } = 100;
    public double Width { get; init; } = 300;
    public double Height { get; init; } = 400;
    public double Opacity { get; init; } = 0.88;
    public double GhostModeOpacity { get; init; } = 0.25;
    public bool GhostModeEnabled { get; init; } = false;
}

public sealed record DictionaryItemData
{
    public string? Word { get; init; }
    public string? Description { get; init; }
}

public sealed record DictionaryWindowState
{
    public double Left { get; init; } = 200;
    public double Top { get; init; } = 150;
    public double Width { get; init; } = 400;
    public double Height { get; init; } = 500;
    public double Opacity { get; init; } = 0.88;
    public double GhostModeOpacity { get; init; } = 0.25;
    public bool GhostModeEnabled { get; init; } = false;
}

/// Specifies where a timestamp should be placed in newly created notes.
public enum NoteTimestampPlacement {
    None,
    Top,
    Bottom
}

/// Controls how clicking a folder item navigates.
public enum FolderNavigationMode {
    DrillDown,  // replace list with folder's contents (default)
    Expand      // expand/collapse inline within the root list
}

/// Manages application settings stored in JSON format in the local app data folder.
public sealed class AppSettingsService : IAppSettingsService {
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

    public FolderNavigationMode LoadFolderNavigationMode() => LoadSetting(s => s.FolderNavigationMode);
    public void SaveFolderNavigationMode(FolderNavigationMode mode) => SaveSetting(s => s with { FolderNavigationMode = mode });

    public IReadOnlyList<ChecklistItemData> LoadChecklistItems()
    {
        return LoadSetting(s => s.ChecklistItems ?? new List<ChecklistItemData>());
    }

    public void SaveChecklistItems(IReadOnlyList<ChecklistItemData> items)
    {
        SaveSetting(s => s with { ChecklistItems = items?.ToList() ?? new List<ChecklistItemData>() });
    }

    public ChecklistWindowState LoadChecklistWindowState()
    {
        return LoadSetting(s => s.ChecklistWindowState ?? new ChecklistWindowState());
    }

    public void SaveChecklistWindowState(ChecklistWindowState state)
    {
        SaveSetting(s => s with { ChecklistWindowState = state });
    }

    public IReadOnlyList<DictionaryItemData> LoadDictionaryItems()
    {
        return LoadSetting(s => s.DictionaryItems ?? new List<DictionaryItemData>());
    }

    public void SaveDictionaryItems(IReadOnlyList<DictionaryItemData> items)
    {
        SaveSetting(s => s with { DictionaryItems = items?.ToList() ?? new List<DictionaryItemData>() });
    }

    public DictionaryWindowState LoadDictionaryWindowState()
    {
        return LoadSetting(s => s.DictionaryWindowState ?? new DictionaryWindowState());
    }

    public void SaveDictionaryWindowState(DictionaryWindowState state)
    {
        SaveSetting(s => s with { DictionaryWindowState = state });
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
        public FolderNavigationMode FolderNavigationMode { get; init; } = FolderNavigationMode.DrillDown;
        public List<ChecklistItemData>? ChecklistItems { get; init; }
        public ChecklistWindowState? ChecklistWindowState { get; init; }
        public List<DictionaryItemData>? DictionaryItems { get; init; }
        public DictionaryWindowState? DictionaryWindowState { get; init; }
    }
}
