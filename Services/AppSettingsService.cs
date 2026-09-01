using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
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
    private const int CurrentSettingsSchemaVersion = 1;
    private const int DefaultMaxSettingsHistoryFiles = 10;
    private static readonly TimeSpan DefaultSettingsHistoryInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFilePath;
    private readonly string _backupFilePath;
    private readonly string _legacyBackupFilePath;
    private readonly string _settingsHistoryDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _settingsHistoryInterval;
    private readonly int _maxSettingsHistoryFiles;
    private AppSettings? _cachedSettings;
    private bool _writesBlocked;
    private string? _lastRaisedWarning;

    public string AppDataDirectory { get; }
    public string? RecoveryNotice { get; private set; }
    public string? LastPersistenceWarning { get; private set; }
    public event Action<string>? PersistenceWarning;

    public AppSettingsService(string? appDataDirectory = null)
        : this(
            appDataDirectory,
            TimeProvider.System,
            DefaultSettingsHistoryInterval,
            DefaultMaxSettingsHistoryFiles) {
    }

    internal AppSettingsService(
            string? appDataDirectory,
            TimeProvider timeProvider,
            TimeSpan settingsHistoryInterval,
            int maxSettingsHistoryFiles) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (settingsHistoryInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settingsHistoryInterval));
        if (maxSettingsHistoryFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSettingsHistoryFiles));

        AppDataDirectory = string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Noted")
            : Path.GetFullPath(appDataDirectory);
        _settingsFilePath = Path.Combine(AppDataDirectory, "settings.json");
        _backupFilePath = $"{_settingsFilePath}.bak";
        _legacyBackupFilePath = Path.Combine(AppDataDirectory, "settings.previous.json");
        _settingsHistoryDirectory = Path.Combine(AppDataDirectory, "backups");
        _timeProvider = timeProvider;
        _settingsHistoryInterval = settingsHistoryInterval;
        _maxSettingsHistoryFiles = maxSettingsHistoryFiles;

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

    public (string modifiers, string key) LoadNotesHotkey()
    {
        var setting = LoadSettings();
        return (setting.NotesHotkeyModifiers ?? "Ctrl+Shift", setting.NotesHotkeyKey ?? "Space");
    }

    public void SaveNotesHotkey(string modifiers, string key)
    {
        SaveSetting(s => s with { NotesHotkeyModifiers = modifiers, NotesHotkeyKey = key });
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

    public IReadOnlyList<ChecklistItemState>? LoadLegacyChecklistItems()
    {
        return LoadSetting(s => s.ChecklistItems?.ToList());
    }

    public void ClearLegacyChecklistItems()
    {
        SaveSetting(s => s with { ChecklistItems = null });
    }

    public ChecklistWindowState LoadChecklistWindowState()
    {
        return LoadSetting(s => s.ChecklistWindowState ?? new ChecklistWindowState());
    }

    public void SaveChecklistWindowState(ChecklistWindowState state)
    {
        SaveSetting(s => s with { ChecklistWindowState = state });
    }

    public IReadOnlyList<DictionaryItemState>? LoadLegacyDictionaryItems()
    {
        return LoadSetting(s => s.DictionaryItems?.ToList());
    }

    public void ClearLegacyDictionaryItems()
    {
        SaveSetting(s => s with { DictionaryItems = null });
    }

    public DictionaryWindowState LoadDictionaryWindowState()
    {
        return LoadSetting(s => s.DictionaryWindowState ?? new DictionaryWindowState());
    }

    public void SaveDictionaryWindowState(DictionaryWindowState state)
    {
        SaveSetting(s => s with { DictionaryWindowState = state });
    }

    public bool LoadMainHideButtonHidesAll()
    {
        return LoadSetting(s => s.MainHideButtonHidesAll);
    }

    public void SaveMainHideButtonHidesAll(bool hidesAll)
    {
        SaveSetting(s => s with { MainHideButtonHidesAll = hidesAll });
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

    public MiniPadWindowState LoadMiniPadWindowState()
    {
        return LoadSetting(s => s.MiniPadWindowState ?? new MiniPadWindowState());
    }

    public void SaveMiniPadWindowState(MiniPadWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SaveSetting(s => s with { MiniPadWindowState = state });
    }

    public bool LoadReopenEditorTabsOnStartup()
    {
        return LoadSetting(s => s.ReopenEditorTabsOnStartup);
    }

    public void SaveReopenEditorTabsOnStartup(bool enabled)
    {
        SaveSetting(s => s with { ReopenEditorTabsOnStartup = enabled });
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

        var settingsFile = ReadSettingsFile(_settingsFilePath);
        if (settingsFile.Status == JsonFileReadStatus.Success) {
            _cachedSettings = settingsFile.Settings!;
            return _cachedSettings;
        }

        if (settingsFile.Status == JsonFileReadStatus.Unavailable) {
            throw CreatePersistenceException(
                $"Noted could not read settings from '{_settingsFilePath}'. No recovery files were changed.",
                settingsFile.Error ?? new IOException("The settings file is unavailable."));
        }

        var recoveryFiles = FindRecoveryFiles();
        var recoveryFilesFound = false;
        var validRecoveryFiles = new List<RecoveryFileReadResult>();
        Exception? lastRecoveryError = null;
        foreach (var recoveryFileInfo in recoveryFiles) {
            var recoveryFile = ReadSettingsFile(recoveryFileInfo.Path);
            if (recoveryFile.Status == JsonFileReadStatus.Missing)
                continue;

            recoveryFilesFound = true;
            if (recoveryFile.Status == JsonFileReadStatus.Unavailable) {
                throw CreatePersistenceException(
                    $"Noted could not inspect the settings recovery file '{recoveryFileInfo.Path}'. No recovery files were changed.",
                    recoveryFile.Error ?? new IOException("The recovery file is unavailable."));
            }

            if (recoveryFile.Status == JsonFileReadStatus.Success) {
                validRecoveryFiles.Add(new RecoveryFileReadResult(
                    recoveryFileInfo,
                    recoveryFile));
                continue;
            }

            lastRecoveryError = recoveryFile.Error;
        }

        var selectedRecoveryFile = SelectRecoveryFile(validRecoveryFiles);
        if (selectedRecoveryFile is null) {
            if (settingsFile.Status == JsonFileReadStatus.Missing && !recoveryFilesFound) {
                _cachedSettings = NormalizeSettings(new AppSettings());
                ValidateSettings(_cachedSettings);
                return _cachedSettings;
            }

            throw CreatePersistenceException(
                settingsFile.Status == JsonFileReadStatus.Missing
                    ? "Noted could not restore missing settings because no valid recovery copy was available. Existing recovery files were not changed."
                    : "Noted found invalid settings but could not restore a valid recovery copy. Existing files were not changed.",
                lastRecoveryError ?? settingsFile.Error ?? new JsonException("No valid settings recovery copy was available."));
        }

        return RecoverSettings(settingsFile, selectedRecoveryFile.SettingsFile);
    }

    private AppSettings RecoverSettings(
            SettingsReadResult settingsFile,
            SettingsReadResult selectedRecoveryFile) {
        var damagedPath = settingsFile.Status == JsonFileReadStatus.Corrupt
            ? CreateUniqueSettingsPath("settings.corrupt", AppDataDirectory)
            : null;

        FileWriteResult writeResult;
        try {
            writeResult = FileWriter.WriteAllText(
                _settingsFilePath,
                selectedRecoveryFile.SerializedJson!,
                damagedPath);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            throw CreatePersistenceException(
                $"Noted found a valid settings recovery copy at '{selectedRecoveryFile.Path}', but could not restore it. The recovery copy was not changed.",
                ex);
        }

        var verification = ReadSettingsFile(_settingsFilePath);
        if (verification.Status != JsonFileReadStatus.Success ||
            !string.Equals(
                verification.SerializedJson,
                selectedRecoveryFile.SerializedJson,
                StringComparison.Ordinal)) {
            _writesBlocked = true;
            throw CreatePersistenceException(
                "Noted restored settings but could not verify the resulting settings file. Further settings writes are blocked until restart. Recovery files were left in place.",
                verification.Error ?? new IOException("The restored settings did not match the selected recovery copy."));
        }

        var preservedDamagedPath = writeResult.BackupUpdated
            ? damagedPath
            : writeResult.PreservedBackupPath;
        var recoveryReason = settingsFile.Status == JsonFileReadStatus.Missing
            ? "settings.json was missing"
            : "settings.json was invalid";
        var preservedMessage = preservedDamagedPath is not null
            ? $" The previous file was preserved as '{Path.GetFileName(preservedDamagedPath)}'."
            : string.Empty;
        var warningMessage = string.IsNullOrWhiteSpace(writeResult.Warning)
            ? string.Empty
            : $"\n\nRecovery protection warning: {writeResult.Warning}";
        RecoveryNotice =
            $"Noted restored settings from '{Path.GetFileName(selectedRecoveryFile.Path)}' because {recoveryReason}." +
            preservedMessage +
            " Review your settings because newer changes may need to be reapplied." +
            warningMessage;
        _cachedSettings = verification.Settings!;
        return _cachedSettings;
    }

    private IReadOnlyList<RecoveryFileInfo> FindRecoveryFiles() {
        try {
            var currentPendingPaths = Directory.GetFiles(
                    AppDataDirectory,
                    $"{Path.GetFileName(_backupFilePath)}.pending-*");
            var legacyPendingPaths = Directory.GetFiles(
                AppDataDirectory,
                $"{Path.GetFileName(_legacyBackupFilePath)}.pending-*");
            string[] historyFiles;
            try {
                historyFiles = Directory.GetFiles(_settingsHistoryDirectory, "settings_*.json");
            } catch (DirectoryNotFoundException) {
                historyFiles = [];
            }

            return currentPendingPaths
                .Concat([_backupFilePath])
                .Concat(legacyPendingPaths)
                .Concat([_legacyBackupFilePath])
                .Concat(historyFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new RecoveryFileInfo(
                    path,
                    File.GetLastWriteTimeUtc(path)))
                .ToList();
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            throw CreatePersistenceException(
                "Noted could not completely inspect settings recovery files. No recovery files were changed.",
                ex);
        }
    }

    private static RecoveryFileReadResult? SelectRecoveryFile(
            IReadOnlyCollection<RecoveryFileReadResult> recoveryFiles) {
        if (recoveryFiles.Count == 0)
            return null;

        var highestRevision = recoveryFiles.Max(
            file => file.SettingsFile.Settings!.Revision);
        IReadOnlyList<RecoveryFileReadResult> newestFiles;
        if (highestRevision > 0) {
            newestFiles = recoveryFiles
                .Where(file => file.SettingsFile.Settings!.Revision == highestRevision)
                .OrderByDescending(file => file.SettingsFile.Settings!.SavedUtc)
                .ThenByDescending(file => file.FileInfo.LastWriteTimeUtc)
                .ThenBy(file => file.FileInfo.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        } else {
            var newestWriteTime = recoveryFiles.Max(file => file.FileInfo.LastWriteTimeUtc);
            newestFiles = recoveryFiles
                .Where(file => file.FileInfo.LastWriteTimeUtc == newestWriteTime)
                .OrderBy(file => file.FileInfo.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var distinctStates = newestFiles
            .Select(file => file.SettingsFile.SerializedJson)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();
        if (distinctStates > 1) {
            var description = highestRevision > 0
                ? $"revision {highestRevision}"
                : $"write time {newestFiles[0].FileInfo.LastWriteTimeUtc:O}";
            throw CreatePersistenceException(
                $"Noted found conflicting settings recovery files with the same {description}. No recovery file was selected or changed.",
                new InvalidDataException("The newest settings recovery files do not contain the same state."));
        }

        return newestFiles[0];
    }

    private void SaveSettings(AppSettings settings) {
        if (_writesBlocked) {
            throw CreatePersistenceException(
                "Settings writes are blocked because a previous settings write could not be verified. Restart Noted before trying again.",
                new IOException("The current settings file has an unverified state."));
        }

        var normalizedSettings = NormalizeSettings(settings);
        ValidateSettings(normalizedSettings);
        var current = ReadSettingsFile(_settingsFilePath);
        if (current.Status == JsonFileReadStatus.Success &&
            SettingsContentEquals(current.Settings!, normalizedSettings)) {
            _cachedSettings = current.Settings;
            return;
        }

        if (current.Status is JsonFileReadStatus.Corrupt or JsonFileReadStatus.Unavailable) {
            _writesBlocked = true;
            throw CreatePersistenceException(
                "Noted did not overwrite settings because the current settings file is invalid or unavailable. Restart Noted to run recovery.",
                current.Error ?? new IOException("The current settings file cannot be safely replaced."));
        }

        var highestKnownRevision = Math.Max(
            normalizedSettings.Revision,
            current.Settings?.Revision ?? 0);
        if (highestKnownRevision == long.MaxValue) {
            _writesBlocked = true;
            throw CreatePersistenceException(
                "Settings cannot be saved because the settings revision reached its maximum value. Further settings writes are blocked until restart.",
                new OverflowException("The settings revision cannot be incremented."));
        }

        var settingsToSave = normalizedSettings with {
            SchemaVersion = CurrentSettingsSchemaVersion,
            Revision = highestKnownRevision + 1,
            SavedUtc = _timeProvider.GetUtcNow()
        };
        ValidateSettings(settingsToSave);
        var json = SerializeSettings(settingsToSave);

        FileWriteResult writeResult;
        try {
            writeResult = FileWriter.WriteAllText(_settingsFilePath, json, _backupFilePath);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            throw CreatePersistenceException(
                $"Noted could not save settings to '{_settingsFilePath}'. Your latest settings change was not saved.",
                ex);
        }

        string persistedJson;
        try {
            persistedJson = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            _writesBlocked = true;
            throw CreatePersistenceException(
                "Noted wrote settings but could not read the settings file back for verification. Further settings writes are blocked until restart.",
                ex);
        }

        var verification = ReadSettingsFile(_settingsFilePath);
        if (verification.Status != JsonFileReadStatus.Success ||
            !string.Equals(persistedJson, json, StringComparison.Ordinal) ||
            !string.Equals(verification.SerializedJson, json, StringComparison.Ordinal)) {
            _writesBlocked = true;
            throw CreatePersistenceException(
                "Noted wrote settings but could not verify the resulting settings file. Further settings writes are blocked until restart.",
                verification.Error ?? new IOException("The persisted settings did not match the requested settings."));
        }

        _cachedSettings = verification.Settings!;
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(writeResult.Warning))
            warnings.Add(writeResult.Warning);
        UpdateSettingsHistory(persistedJson, warnings);
        SetPersistenceWarning(warnings);
    }

    private void UpdateSettingsHistory(string settingsJson, ICollection<string> warnings) {
        try {
            Directory.CreateDirectory(_settingsHistoryDirectory);
            var historyFiles = GetSettingsHistoryFiles();
            var newestValidHistoryFile = historyFiles
                .Select(info => new {
                    File = info,
                    ReadResult = ReadSettingsFile(info.FullName)
                })
                .FirstOrDefault(item => item.ReadResult.Status == JsonFileReadStatus.Success);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (newestValidHistoryFile is not null) {
                var historyAge = now - newestValidHistoryFile.File.LastWriteTimeUtc;
                if (historyAge >= TimeSpan.Zero && historyAge < _settingsHistoryInterval)
                    return;
            }

            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(settingsJson)))[..12];
            var historyPath = Path.Combine(
                _settingsHistoryDirectory,
                $"settings_{now:yyyyMMdd_HHmmss_fff}_{hash}_{Guid.NewGuid():N}.json");
            FileWriter.WriteAllText(historyPath, settingsJson);
            var verification = ReadSettingsFile(historyPath);
            if (verification.Status != JsonFileReadStatus.Success ||
                !string.Equals(verification.SerializedJson, settingsJson, StringComparison.Ordinal)) {
                throw new IOException("The new settings history file could not be verified.");
            }

            File.SetLastWriteTimeUtc(historyPath, now);
            PruneSettingsHistory();
        } catch (Exception ex) when (ex is JsonException || IsExpectedSettingsIoException(ex)) {
            warnings.Add($"Settings were saved, but settings history could not be updated: {ex.Message}");
        }
    }

    private IReadOnlyList<FileInfo> GetSettingsHistoryFiles() {
        if (!Directory.Exists(_settingsHistoryDirectory))
            return [];

        return Directory.GetFiles(_settingsHistoryDirectory, "settings_*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ThenBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void PruneSettingsHistory() {
        var oldHistoryFiles = GetSettingsHistoryFiles()
            .Where(info => ReadSettingsFile(info.FullName).Status == JsonFileReadStatus.Success)
            .Skip(_maxSettingsHistoryFiles)
            .ToList();
        foreach (var historyFile in oldHistoryFiles)
            FileWriter.DeleteIfExists(historyFile.FullName);
    }

    private void SetPersistenceWarning(IReadOnlyCollection<string> warnings) {
        LastPersistenceWarning = warnings.Count == 0
            ? null
            : string.Join(" ", warnings.Distinct(StringComparer.Ordinal));
        if (LastPersistenceWarning is null) {
            _lastRaisedWarning = null;
            return;
        }
        if (string.Equals(LastPersistenceWarning, _lastRaisedWarning, StringComparison.Ordinal))
            return;

        _lastRaisedWarning = LastPersistenceWarning;
        var subscribers = PersistenceWarning;
        if (subscribers is null)
            return;

        foreach (Action<string> subscriber in subscribers.GetInvocationList()) {
            try {
                subscriber(LastPersistenceWarning);
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine(
                    $"A settings persistence warning subscriber failed: {ex}");
            }
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
            SchemaVersion = CurrentSettingsSchemaVersion,
            NotesDirectory = string.IsNullOrWhiteSpace(settings.NotesDirectory)
                ? null
                : settings.NotesDirectory,
            TimestampPlacement = Enum.IsDefined(settings.TimestampPlacement)
                ? settings.TimestampPlacement
                : NoteTimestampPlacement.None,
            NewNoteMode = mode,
            PromptForNoteName = null,
            PinnedNotes = settings.PinnedNotes?
                .Where(note => note is not null)
                .ToList(),
            NotesHotkeyModifiers = string.IsNullOrWhiteSpace(settings.NotesHotkeyModifiers) ? "Ctrl+Shift" : settings.NotesHotkeyModifiers,
            NotesHotkeyKey = string.IsNullOrWhiteSpace(settings.NotesHotkeyKey) ? "Space" : settings.NotesHotkeyKey,
            ChecklistHotkeyModifiers = string.IsNullOrWhiteSpace(settings.ChecklistHotkeyModifiers) ? "Alt" : settings.ChecklistHotkeyModifiers,
            ChecklistHotkeyKey = string.IsNullOrWhiteSpace(settings.ChecklistHotkeyKey) ? "C" : settings.ChecklistHotkeyKey,
            DictionaryHotkeyModifiers = string.IsNullOrWhiteSpace(settings.DictionaryHotkeyModifiers) ? "Alt" : settings.DictionaryHotkeyModifiers,
            DictionaryHotkeyKey = string.IsNullOrWhiteSpace(settings.DictionaryHotkeyKey) ? "D" : settings.DictionaryHotkeyKey,
            GhostModeOpacity = NormalizeOpacity(settings.GhostModeOpacity, 0, 0.25),
            DefaultOpacity = NormalizeOpacity(settings.DefaultOpacity, 0.05, 0.88),
            FolderNavigationMode = Enum.IsDefined(settings.FolderNavigationMode)
                ? settings.FolderNavigationMode
                : FolderNavigationMode.DrillDown,
            ChecklistItems = settings.ChecklistItems?
                .Where(item => item is not null)
                .Select(item => Enum.IsDefined(item.Priority)
                    ? item
                    : item with { Priority = ChecklistPriority.None })
                .ToList(),
            ChecklistWindowState = (settings.ChecklistWindowState ?? new ChecklistWindowState()) with
            {
                PriorityColors = ChecklistColors.Normalize(
                    settings.ChecklistWindowState?.PriorityColors)
            },
            DictionaryItems = settings.DictionaryItems?
                .Where(item => item is not null)
                .ToList()
        };
    }

    private static void ValidateSettings(AppSettings settings) {
        if (settings.SchemaVersion != CurrentSettingsSchemaVersion)
            throw new JsonException("The settings schema version is not supported.");
        if (settings.Revision < 0)
            throw new JsonException("The settings revision cannot be negative.");
        if (!settings.NewNoteMode.HasValue || !Enum.IsDefined(settings.NewNoteMode.Value))
            throw new JsonException("The note creation mode is not recognized.");
        if (settings.PinnedNotes?.Any(note => note is null) == true)
            throw new JsonException("Pinned notes cannot contain an empty entry.");
        if (settings.ChecklistItems?.Any(item => item is null || !Enum.IsDefined(item.Priority)) == true)
            throw new JsonException("Legacy checklist items contain an invalid entry.");
        if (settings.DictionaryItems?.Any(item => item is null) == true)
            throw new JsonException("Legacy dictionary items contain an invalid entry.");
    }

    private static double NormalizeOpacity(double value, double minimum, double fallback) =>
        double.IsFinite(value)
            ? Math.Clamp(value, minimum, 1)
            : fallback;

    private static SettingsReadResult ReadSettingsFile(string path) {
        try {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJsonOptions)
                ?? throw new JsonException("The settings document did not contain a JSON object.");
            if (settings.SchemaVersion > CurrentSettingsSchemaVersion) {
                return new SettingsReadResult(
                    path,
                    JsonFileReadStatus.Unavailable,
                    null,
                    null,
                    new NotSupportedException(
                        $"Settings schema version {settings.SchemaVersion} is newer than the supported version {CurrentSettingsSchemaVersion}."));
            }
            var normalizedSettings = NormalizeSettings(settings);
            ValidateSettings(normalizedSettings);
            return new SettingsReadResult(
                path,
                JsonFileReadStatus.Success,
                normalizedSettings,
                SerializeSettings(normalizedSettings),
                null);
        } catch (FileNotFoundException) {
            return new SettingsReadResult(path, JsonFileReadStatus.Missing, null, null, null);
        } catch (DirectoryNotFoundException) {
            return new SettingsReadResult(path, JsonFileReadStatus.Missing, null, null, null);
        } catch (JsonException ex) {
            return new SettingsReadResult(path, JsonFileReadStatus.Corrupt, null, null, ex);
        } catch (Exception ex) when (IsExpectedSettingsIoException(ex)) {
            return new SettingsReadResult(path, JsonFileReadStatus.Unavailable, null, null, ex);
        }
    }

    private static string SerializeSettings(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonOptions);

    private static bool SettingsContentEquals(AppSettings left, AppSettings right) =>
        string.Equals(
            SerializeSettings(left with { Revision = 0, SavedUtc = null }),
            SerializeSettings(right with { Revision = 0, SavedUtc = null }),
            StringComparison.Ordinal);

    private static bool IsExpectedSettingsIoException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException;

    private static SettingsPersistenceException CreatePersistenceException(string message, Exception innerException) =>
        new(message, innerException);

    private string CreateUniqueSettingsPath(string fileName, string directory) {
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss_fff");
        var path = Path.Combine(directory, $"{fileName}_{timestamp}.json");
        var suffix = 1;

        while (File.Exists(path)) {
            path = Path.Combine(directory, $"{fileName}_{timestamp}_{suffix:00}.json");
            suffix++;
        }

        return path;
    }

    private sealed record SettingsReadResult(
        string Path,
        JsonFileReadStatus Status,
        AppSettings? Settings,
        string? SerializedJson,
        Exception? Error);

    private sealed record RecoveryFileInfo(
        string Path,
        DateTime LastWriteTimeUtc);

    private sealed record RecoveryFileReadResult(
        RecoveryFileInfo FileInfo,
        SettingsReadResult SettingsFile);

    /// Represents the application settings stored in JSON.
    private sealed record AppSettings {
        public int SchemaVersion { get; init; } = CurrentSettingsSchemaVersion;
        public long Revision { get; init; }
        public DateTimeOffset? SavedUtc { get; init; }
        public string? NotesDirectory { get; init; }
        public NoteTimestampPlacement TimestampPlacement { get; init; } = NoteTimestampPlacement.None;
        [JsonConverter(typeof(NewNoteModeJsonConverter))]
        public NewNoteMode? NewNoteMode { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? PromptForNoteName { get; init; }
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
        public bool GhostModeEnabled { get; init; } = false;
        public double GhostModeOpacity { get; init; } = 0.25;
        public double DefaultOpacity { get; init; } = 0.88;
        public FolderNavigationMode FolderNavigationMode { get; init; } = FolderNavigationMode.DrillDown;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChecklistItemState>? ChecklistItems { get; init; }
        public ChecklistWindowState? ChecklistWindowState { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DictionaryItemState>? DictionaryItems { get; init; }
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
}
