using System.IO;
using System.Text.Json;
namespace MyNotes.Services;

public enum NoteTimestampPlacement {
    None,
    Top,
    Bottom
}

public sealed class AppSettingsService {
    private readonly string _settingsFilePath;

    public string StorageDirectory { get; }

    public AppSettingsService() {
        StorageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyNotes");
        Directory.CreateDirectory(StorageDirectory);
        _settingsFilePath = Path.Combine(StorageDirectory, "settings.json");
    }

    public bool LoadShowModifiedSubtitle() {
        return LoadSettings().ShowModifiedSubtitle;
    }

    public void SaveShowModifiedSubtitle(bool showModifiedSubtitle) {
        var settings = LoadSettings() with {
            ShowModifiedSubtitle = showModifiedSubtitle
        };
        SaveSettings(settings);
    }

    public string? LoadNotesDirectory() {
        var settings = LoadSettings();
        return string.IsNullOrWhiteSpace(settings.NotesDirectory)
            ? null
            : settings.NotesDirectory;
    }

    public void SaveNotesDirectory(string notesDirectory) {
        var settings = LoadSettings() with {
            NotesDirectory = notesDirectory
        };
        SaveSettings(settings);
    }

    public NoteTimestampPlacement LoadTimestampPlacement() {
        return LoadSettings().TimestampPlacement;
    }

    public void SaveTimestampPlacement(NoteTimestampPlacement timestampPlacement) {
        var settings = LoadSettings() with {
            TimestampPlacement = timestampPlacement
        };
        SaveSettings(settings);
    }

    public bool LoadPromptForNoteName() {
        return LoadSettings().PromptForNoteName;
    }

    public void SavePromptForNoteName(bool promptForNoteName) {
        var settings = LoadSettings() with {
            PromptForNoteName = promptForNoteName
        };
        SaveSettings(settings);
    }

    private AppSettings LoadSettings() {
        if (!File.Exists(_settingsFilePath))
            return new AppSettings();

        try {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        } catch {
            return new AppSettings();
        }
    }

    private void SaveSettings(AppSettings settings) {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions {
            WriteIndented = true
        });
        File.WriteAllText(_settingsFilePath, json);
    }

    public string? LoadCustomHeader()
    {
        return LoadSettings().CustomHeader;
    }

    public void SaveCustomHeader(string? customHeader)
    {
        var settings = LoadSettings() with
        {
            CustomHeader = customHeader
        };
        SaveSettings(settings);
    }

    private sealed record AppSettings
    {
        public string? NotesDirectory { get; init; }
        public NoteTimestampPlacement TimestampPlacement { get; init; } = NoteTimestampPlacement.None;
        public bool PromptForNoteName { get; init; }
        public bool ShowModifiedSubtitle { get; init; } = true;
        public string? CustomHeader { get; init; }
    }
}