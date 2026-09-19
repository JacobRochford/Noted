using System.Globalization;
using System.Windows.Input;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

internal enum SettingsAction { ChangeFolder, CreateBackup, ViewBackup, ExportBackup, ImportBackup, RestoreBackup }
internal sealed record SettingsNotice(string Message, string Title, bool IsWarning = true);
internal sealed record AppearanceOption(AppThemeMode Mode, string Label);
internal sealed record DisplayOption(string? DeviceName, string Label);

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsService _settings;
    private readonly INoteFileService _files;
    private readonly IRunOnStartupService _startup;
    private readonly HotkeyConfiguration _hotkeys;
    private readonly Action<AppThemeMode, string> _applyTheme;
    private readonly Func<IReadOnlyList<DisplayInfo>> _getDisplays;
    private bool _loading;
    private HotkeyFeature? _editingFeature;
    private string _activeAccent = AppTheme.DefaultAccentColor;

    internal event Action<string>? PreferenceChanged;
    internal event Action<SettingsNotice>? NoticeRaised;
    public IReadOnlyList<AppearanceOption> AppearanceOptions { get; } = new[]
    {
        new AppearanceOption(AppThemeMode.System, "Use Windows setting"),
        new AppearanceOption(AppThemeMode.Light, "Light"),
        new AppearanceOption(AppThemeMode.Dark, "Dark"),
        new AppearanceOption(AppThemeMode.Midnight, "Midnight blue")
    };
    public IReadOnlyList<string> HotkeyKeys => HotkeyConstants.ValidKeys;
    public IReadOnlyList<DisplayOption> DisplayOptions { get; private set; } = Array.Empty<DisplayOption>();
    internal bool LastBackupAttemptFailed { get; set; }
    internal void RefreshNotesDirectory() => OnPropertyChanged(nameof(NotesDirectory));
    public string NotesDirectory => _files.NotesDirectory;
    public string ToggleNotesDirectoryText => ShowNotesDirectory ? "Hide path" : "Show path";
    public string GhostOpacityLabel => $"{(int)GhostOpacityPercent}%";
    public string DefaultOpacityLabel => $"{(int)DefaultOpacityPercent}%";
    public string ActiveAccent => _activeAccent;
    public string? PreferredDisplayDeviceName { get; private set; }

    public ICommand ChangeFolderCommand { get; }
    public ICommand CreateBackupCommand { get; }
    public ICommand ViewBackupCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand ImportBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand ToggleNotesDirectoryCommand { get; }
    public ICommand ResetAppearanceCommand { get; }
    public ICommand ApplyHotkeyCommand { get; }
    public ICommand ResetHotkeyCommand { get; }
    public ICommand CancelHotkeyCommand { get; }

    internal SettingsViewModel(IAppSettingsService settings, INoteFileService files,
        IRunOnStartupService startup, HotkeyConfiguration hotkeys,
        Action<AppThemeMode, string> applyTheme, Action<SettingsAction> requestAction,
        Func<IReadOnlyList<DisplayInfo>>? getDisplays = null)
    {
        _settings = settings;
        _files = files;
        _startup = startup;
        _hotkeys = hotkeys;
        _applyTheme = applyTheme;
        _getDisplays = getDisplays ?? DisplayService.GetDisplays;
        ChangeFolderCommand = new SettingsCommand(() => requestAction(SettingsAction.ChangeFolder));
        CreateBackupCommand = new SettingsCommand(() => requestAction(SettingsAction.CreateBackup), () => CanCreateBackup);
        ViewBackupCommand = new SettingsCommand(() => requestAction(SettingsAction.ViewBackup), () => CanViewBackup);
        ExportBackupCommand = new SettingsCommand(() => requestAction(SettingsAction.ExportBackup), () => CanExportBackup);
        ImportBackupCommand = new SettingsCommand(() => requestAction(SettingsAction.ImportBackup), () => CanImportBackup);
        RestoreBackupCommand = new SettingsCommand(() => requestAction(SettingsAction.RestoreBackup), () => CanRestoreBackup);
        ToggleNotesDirectoryCommand = new SettingsCommand(() => ShowNotesDirectory = !ShowNotesDirectory);
        ResetAppearanceCommand = new SettingsCommand(ResetAppearance);
        ApplyHotkeyCommand = new SettingsCommand(ApplyHotkey, () => _editingFeature.HasValue);
        ResetHotkeyCommand = new SettingsCommand(ResetHotkey, () => _editingFeature.HasValue);
        CancelHotkeyCommand = new SettingsCommand(() => IsHotkeyEditorOpen = false);
        Reload();
    }

    private void Save(Action save, string property)
    {
        if (_loading) return;
        if (TrySave(save)) PreferenceChanged?.Invoke(property);
    }

    private bool TrySave(Action save)
    {
        try
        {
            save();
            return true;
        }
        catch (SettingsPersistenceException exception)
        {
            ExceptionDiagnostics.Record(exception);
            NoticeRaised?.Invoke(new(exception.Message, "Noted - Settings Error"));
            return false;
        }
    }

    private void NotifyAll()
    {
        OnPropertyChanged(string.Empty);
        foreach (var command in new[] { CreateBackupCommand, ViewBackupCommand, ExportBackupCommand,
            ImportBackupCommand, RestoreBackupCommand, ApplyHotkeyCommand, ResetHotkeyCommand })
            ((SettingsCommand)command).Refresh();
    }

    internal void Reload()
    {
        _loading = true;
        try
        {
            NewNoteMode = _settings.LoadNewNoteMode();
            TimestampPlacement = _settings.LoadTimestampPlacement();
            TimestampLineText = _settings.LoadTimestampLine().ToString(CultureInfo.InvariantCulture);
            ShowModifiedSubtitle = _settings.LoadShowModifiedSubtitle();
            ConfirmNoteDeletion = _settings.LoadConfirmNoteDeletion();
            ThemeMode = _settings.LoadAppThemeMode();
            _activeAccent = _settings.LoadAccentColor();
            AccentText = _activeAccent;
            AccentMessage = $"Using {_activeAccent}";
            AccentHasError = false;
            GhostModeEnabled = _settings.LoadGhostModeEnabled();
            GhostOpacityPercent = NormalizeOpacity(_settings.LoadGhostModeOpacity(), 0, 0.25) * 100;
            DefaultOpacityPercent = NormalizeOpacity(_settings.LoadDefaultOpacity(), 0.05, 0.88) * 100;
            RunOnStartup = _startup.IsRunOnStartupEnabled;
            MainHideButtonHidesAll = _settings.LoadMainHideButtonHidesAll();
            PreferredDisplayDeviceName = _settings.LoadPreferredDisplayDeviceName();
            var screens = _getDisplays();
            var primary = screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens.FirstOrDefault();
            var options = new List<DisplayOption>
            {
                new(null, primary is null ? "Windows primary display" : $"Windows primary display ({FormatDisplayName(primary.DeviceName)})")
            };
            options.AddRange(screens.OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
                .Select(screen => new DisplayOption(screen.DeviceName,
                    $"{FormatDisplayName(screen.DeviceName)}  {screen.WorkWidth} × {screen.WorkHeight}" +
                    (screen.IsPrimary ? "  (primary)" : string.Empty))));
            DisplayOptions = options;
            _preferredDisplay = options.FirstOrDefault(option => string.Equals(option.DeviceName,
                PreferredDisplayDeviceName, StringComparison.OrdinalIgnoreCase)) ?? options[0];
            FolderNavigationMode = _settings.LoadFolderNavigationMode() == FolderNavigationMode.Expand
                ? FolderNavigationMode.Expand : FolderNavigationMode.DrillDown;
            SyncHotkeys(true);
        }
        finally { _loading = false; }
        NotifyAll();
    }

    private NewNoteMode _newNoteMode = NewNoteMode.Prompt;
    public NewNoteMode NewNoteMode
    {
        get => _newNoteMode;
        set
        {
            if (!SetProperty(ref _newNoteMode, value)) return;
            Save(() => _settings.SaveNewNoteMode(value), nameof(NewNoteMode));
            NotifyAll();
        }
    }

    private NoteTimestampPlacement _timestampPlacement = NoteTimestampPlacement.None;
    public NoteTimestampPlacement TimestampPlacement
    {
        get => _timestampPlacement;
        set
        {
            if (!SetProperty(ref _timestampPlacement, value)) return;
            Save(() => _settings.SaveTimestampPlacement(value), nameof(TimestampPlacement));
            NotifyAll();
        }
    }

    private bool _showModifiedSubtitle = true;
    public bool ShowModifiedSubtitle
    {
        get => _showModifiedSubtitle;
        set
        {
            if (!SetProperty(ref _showModifiedSubtitle, value)) return;
            Save(() => _settings.SaveShowModifiedSubtitle(value), nameof(ShowModifiedSubtitle));
            NotifyAll();
        }
    }

    private bool _confirmNoteDeletion = true;
    public bool ConfirmNoteDeletion
    {
        get => _confirmNoteDeletion;
        set
        {
            if (!SetProperty(ref _confirmNoteDeletion, value)) return;
            Save(() => _settings.SaveConfirmNoteDeletion(value), nameof(ConfirmNoteDeletion));
            NotifyAll();
        }
    }

    private FolderNavigationMode _folderNavigationMode = FolderNavigationMode.DrillDown;
    public FolderNavigationMode FolderNavigationMode
    {
        get => _folderNavigationMode;
        set
        {
            if (!SetProperty(ref _folderNavigationMode, value)) return;
            Save(() => _settings.SaveFolderNavigationMode(value), nameof(FolderNavigationMode));
            NotifyAll();
        }
    }

    private bool _mainHideButtonHidesAll = false;
    public bool MainHideButtonHidesAll
    {
        get => _mainHideButtonHidesAll;
        set
        {
            if (!SetProperty(ref _mainHideButtonHidesAll, value)) return;
            Save(() => _settings.SaveMainHideButtonHidesAll(value), nameof(MainHideButtonHidesAll));
            NotifyAll();
        }
    }

    private bool _ghostModeEnabled = false;
    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set
        {
            if (!SetProperty(ref _ghostModeEnabled, value)) return;
            Save(() => _settings.SaveGhostModeEnabled(value), nameof(GhostModeEnabled));
            NotifyAll();
        }
    }
    public bool NewNoteModePrompt { get => NewNoteMode == NewNoteMode.Prompt; set { if (value) NewNoteMode = NewNoteMode.Prompt; } }
    public bool NewNoteModeQuick { get => NewNoteMode == NewNoteMode.Quick; set { if (value) NewNoteMode = NewNoteMode.Quick; } }
    public bool NewNoteModeBoth { get => NewNoteMode == NewNoteMode.Both; set { if (value) NewNoteMode = NewNoteMode.Both; } }
    public bool TimestampPlacementNone { get => TimestampPlacement == NoteTimestampPlacement.None; set { if (value) TimestampPlacement = NoteTimestampPlacement.None; } }
    public bool TimestampPlacementTop { get => TimestampPlacement == NoteTimestampPlacement.Top; set { if (value) TimestampPlacement = NoteTimestampPlacement.Top; } }
    public bool TimestampPlacementBottom { get => TimestampPlacement == NoteTimestampPlacement.Bottom; set { if (value) TimestampPlacement = NoteTimestampPlacement.Bottom; } }
    public bool TimestampPlacementLine { get => TimestampPlacement == NoteTimestampPlacement.Line; set { if (value) TimestampPlacement = NoteTimestampPlacement.Line; } }
    public bool FolderNavigationModeDrillDown { get => FolderNavigationMode == FolderNavigationMode.DrillDown; set { if (value) FolderNavigationMode = FolderNavigationMode.DrillDown; } }
    public bool FolderNavigationModeExpand { get => FolderNavigationMode == FolderNavigationMode.Expand; set { if (value) FolderNavigationMode = FolderNavigationMode.Expand; } }
    private bool _canCreateBackup = true;
    public bool CanCreateBackup { get => _canCreateBackup; internal set { if (SetProperty(ref _canCreateBackup, value)) NotifyAll(); } }
    private bool _canViewBackup = true;
    public bool CanViewBackup { get => _canViewBackup; internal set { if (SetProperty(ref _canViewBackup, value)) NotifyAll(); } }
    private bool _canExportBackup = true;
    public bool CanExportBackup { get => _canExportBackup; internal set { if (SetProperty(ref _canExportBackup, value)) NotifyAll(); } }
    private bool _canImportBackup = true;
    public bool CanImportBackup { get => _canImportBackup; internal set { if (SetProperty(ref _canImportBackup, value)) NotifyAll(); } }
    private bool _canRestoreBackup = true;
    public bool CanRestoreBackup { get => _canRestoreBackup; internal set { if (SetProperty(ref _canRestoreBackup, value)) NotifyAll(); } }
    private string _backupStatus = "";
    public string BackupStatus { get => _backupStatus; internal set => SetProperty(ref _backupStatus, value); }
    private string _backupStatusResource = "NotedSecondaryTextBrush";
    public string BackupStatusResource { get => _backupStatusResource; internal set => SetProperty(ref _backupStatusResource, value); }
    private string _createBackupText = "Create Backup";
    public string CreateBackupText { get => _createBackupText; internal set => SetProperty(ref _createBackupText, value); }
    private string _accentMessage = "";
    public string AccentMessage { get => _accentMessage; internal set => SetProperty(ref _accentMessage, value); }
    private string _timestampLineText = "";
    public string TimestampLineText { get => _timestampLineText; set => SetProperty(ref _timestampLineText, value); }

    private bool _showNotesDirectory;
    public bool ShowNotesDirectory
    {
        get => _showNotesDirectory;
        set { if (SetProperty(ref _showNotesDirectory, value)) { OnPropertyChanged(nameof(ToggleNotesDirectoryText)); PreferenceChanged?.Invoke(nameof(ShowNotesDirectory)); } }
    }
    private bool _accentHasError;
    public bool AccentHasError { get => _accentHasError; private set => SetProperty(ref _accentHasError, value); }
    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set
        {
            if (_loading) { _runOnStartup = value; return; }
            if (value == _runOnStartup) return;
            var result = _startup.SetRunOnStartup(value);
            _runOnStartup = result.ActualEnabled ?? _runOnStartup;
            OnPropertyChanged();
            if (!result.Success || result.ActualEnabled is null || result.ActualEnabled != value)
                NoticeRaised?.Invoke(new(result.Error ?? "Windows did not apply the requested startup setting.", "Startup Setting Failed"));
        }
    }
    private DisplayOption? _preferredDisplay;
    public DisplayOption? PreferredDisplay
    {
        get => _preferredDisplay;
        set
        {
            if (value is null || !SetProperty(ref _preferredDisplay, value)) return;
            if (_loading) return;
            if (!TrySave(() => _settings.SavePreferredDisplayDeviceName(value.DeviceName))) return;
            PreferredDisplayDeviceName = value.DeviceName;
            PreferenceChanged?.Invoke(nameof(PreferredDisplay));
        }
    }
    private double _ghostOpacityPercent;
    public double GhostOpacityPercent
    {
        get => _ghostOpacityPercent;
        set
        {
            var normalized = NormalizeOpacity(value / 100, 0, 0.25);
            if (!SetProperty(ref _ghostOpacityPercent, normalized * 100)) return;
            Save(() => _settings.SaveGhostModeOpacity(normalized), nameof(GhostOpacityPercent));
            OnPropertyChanged(nameof(GhostOpacityLabel));
        }
    }
    private double _defaultOpacityPercent;
    public double DefaultOpacityPercent
    {
        get => _defaultOpacityPercent;
        set
        {
            var normalized = NormalizeOpacity(value / 100, 0.05, 0.88);
            if (!SetProperty(ref _defaultOpacityPercent, normalized * 100)) return;
            Save(() => _settings.SaveDefaultOpacity(normalized), nameof(DefaultOpacityPercent));
            OnPropertyChanged(nameof(DefaultOpacityLabel));
        }
    }
    private AppThemeMode _themeMode;
    public AppThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (_loading) { _themeMode = value; return; }
            ApplyAppearance(value, _activeAccent);
        }
    }
    private string _accentText = "";
    public string AccentText
    {
        get => _accentText;
        set
        {
            if (!SetProperty(ref _accentText, value) || _loading) return;
            if (!AppTheme.TryNormalizeAccentColor(value, out var color))
            {
                AccentMessage = "Use a six-digit HEX color, such as #5BA8C8.";
                AccentHasError = true;
                return;
            }
            ApplyAppearance(_themeMode, color);
        }
    }
    private void ApplyAppearance(AppThemeMode mode, string accent)
    {
        if (!TrySave(() => _settings.SaveAppTheme(mode, accent))) return;
        _applyTheme(mode, accent);
        _themeMode = mode;
        _activeAccent = AppTheme.NormalizeAccentColor(accent);
        AccentMessage = $"Using {_activeAccent}";
        AccentHasError = false;
        OnPropertyChanged(nameof(ThemeMode));
    }
    internal void NormalizeAccentText()
    {
        var color = AppTheme.TryNormalizeAccentColor(AccentText, out var normalized) ? normalized : _activeAccent;
        if (string.Equals(AccentText, color, StringComparison.Ordinal)) return;
        _accentText = color;
        OnPropertyChanged(nameof(AccentText));
        AccentMessage = $"Using {color}";
        AccentHasError = false;
    }
    private void ResetAppearance()
    {
        _accentText = AppTheme.DefaultAccentColor;
        _themeMode = AppThemeMode.System;
        OnPropertyChanged(nameof(AccentText));
        OnPropertyChanged(nameof(ThemeMode));
        ApplyAppearance(AppThemeMode.System, AppTheme.DefaultAccentColor);
    }
    internal void CommitTimestampLine()
    {
        if (!int.TryParse(TimestampLineText, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
            line = _settings.LoadTimestampLine();
        line = Math.Clamp(line, 1, 10000);
        TimestampLineText = line.ToString(CultureInfo.InvariantCulture);
        TrySave(() => _settings.SaveTimestampLine(line));
    }
    private static double NormalizeOpacity(double value, double minimum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, 1.0) : fallback;

    public string NotesHotkey => HotkeyText(HotkeyFeature.Notes);
    public string ChecklistHotkey => HotkeyText(HotkeyFeature.Checklist);
    public string DictionaryHotkey => HotkeyText(HotkeyFeature.Dictionary);
    public string? NotesHotkeyTooltip => HotkeyTooltip(HotkeyFeature.Notes);
    public string? ChecklistHotkeyTooltip => HotkeyTooltip(HotkeyFeature.Checklist);
    public string? DictionaryHotkeyTooltip => HotkeyTooltip(HotkeyFeature.Dictionary);
    private string HotkeyText(HotkeyFeature feature) => _hotkeys.GetHotkeyRegistration(feature) is not { } registration
        ? "Not registered" : _hotkeys.GetAdditionalHotkeyRegistration(feature) is not null ? "Multiple active" : registration.Combination;
    private string? HotkeyTooltip(HotkeyFeature feature) =>
        _hotkeys.GetHotkeyRegistration(feature) is { } first && _hotkeys.GetAdditionalHotkeyRegistration(feature) is { } second
            ? $"{first.Combination} and {second.Combination}" : null;
    public string HotkeyEditorTitle => _editingFeature is { } feature ? $"{HotkeyConfiguration.GetHotkeyFeatureName(feature)} hotkey" : "";
    private bool _isHotkeyEditorOpen;
    public bool IsHotkeyEditorOpen
    {
        get => _isHotkeyEditorOpen;
        set { if (SetProperty(ref _isHotkeyEditorOpen, value) && !value) { _editingFeature = null; NotifyAll(); } }
    }
    private bool _hotkeyCtrl = false;
    public bool HotkeyCtrl { get => _hotkeyCtrl; set { if (SetProperty(ref _hotkeyCtrl, value)) OnPropertyChanged(nameof(HotkeyPreview)); } }
    private bool _hotkeyAlt = false;
    public bool HotkeyAlt { get => _hotkeyAlt; set { if (SetProperty(ref _hotkeyAlt, value)) OnPropertyChanged(nameof(HotkeyPreview)); } }
    private bool _hotkeyShift = false;
    public bool HotkeyShift { get => _hotkeyShift; set { if (SetProperty(ref _hotkeyShift, value)) OnPropertyChanged(nameof(HotkeyPreview)); } }
    private bool _hotkeyWin = false;
    public bool HotkeyWin { get => _hotkeyWin; set { if (SetProperty(ref _hotkeyWin, value)) OnPropertyChanged(nameof(HotkeyPreview)); } }
    private string? _hotkeyKey = null;
    public string? HotkeyKey { get => _hotkeyKey; set { if (SetProperty(ref _hotkeyKey, value)) OnPropertyChanged(nameof(HotkeyPreview)); } }

    private List<string> SelectedModifiers()
    {
        var selected = new List<string>();
        if (HotkeyCtrl) selected.Add("Ctrl");
        if (HotkeyAlt) selected.Add("Alt");
        if (HotkeyShift) selected.Add("Shift");
        if (HotkeyWin) selected.Add("Win");
        return selected;
    }
    public string HotkeyPreview => HotkeyConstants.BuildHotkey(SelectedModifiers(), HotkeyKey) ?? "Select modifiers and a key";
    internal void BeginHotkeyEdit(HotkeyFeature feature)
    {
        _editingFeature = feature;
        var registration = _hotkeys.GetHotkeyRegistration(feature);
        var (modifiers, key) = registration is not null && _hotkeys.GetAdditionalHotkeyRegistration(feature) is null
            ? (registration.Modifiers, registration.Key) : _hotkeys.LoadHotkey(feature);
        SetHotkeyEditor(modifiers, key);
        IsHotkeyEditorOpen = true;
        NotifyAll();
    }
    private void SetHotkeyEditor(string modifiers, string key)
    {
        HotkeyKey = HotkeyKeys.Contains(key) ? key : null;
        var selected = modifiers.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HotkeyCtrl = selected.Contains("Ctrl");
        HotkeyAlt = selected.Contains("Alt");
        HotkeyShift = selected.Contains("Shift");
        HotkeyWin = selected.Contains("Win");
    }
    internal void SyncHotkeys(bool restoreEditorToActive)
    {
        if (restoreEditorToActive && _editingFeature is { } feature
            && _hotkeys.GetHotkeyRegistration(feature) is { } registration
            && _hotkeys.GetAdditionalHotkeyRegistration(feature) is null)
            SetHotkeyEditor(registration.Modifiers, registration.Key);
        NotifyAll();
    }
    private void ResetHotkey()
    {
        if (_editingFeature is not { } feature) return;
        var (modifiers, key) = HotkeyConfiguration.GetDefaultHotkey(feature);
        SetHotkeyEditor(modifiers, key);
    }
    private void ApplyHotkey()
    {
        if (_editingFeature is not { } feature) return;
        var result = _hotkeys.Change(feature, SelectedModifiers(), HotkeyKey);
        var restoreEditor = result.Status is HotkeyChangeStatus.Invalid or HotkeyChangeStatus.Unchanged
            or HotkeyChangeStatus.Saved or HotkeyChangeStatus.SaveFailed
            || result.Status == HotkeyChangeStatus.Failed
                && _hotkeys.GetAdditionalHotkeyRegistration(feature) is null
                && _hotkeys.GetHotkeyRegistration(feature) is not null;
        SyncHotkeys(restoreEditor);
        if (result.Status is HotkeyChangeStatus.Saved or HotkeyChangeStatus.Unchanged)
            IsHotkeyEditorOpen = false;
        if (result.Status == HotkeyChangeStatus.Unchanged) return;
        var title = result.Status switch
        {
            HotkeyChangeStatus.InvalidSelection or HotkeyChangeStatus.Invalid => "Invalid Hotkey Selection",
            HotkeyChangeStatus.Ambiguous => "Hotkey State Is Ambiguous",
            HotkeyChangeStatus.SaveFailed => "Hotkey Active but Not Saved",
            HotkeyChangeStatus.Saved => "Hotkey Updated",
            HotkeyChangeStatus.Failed when _hotkeys.GetAdditionalHotkeyRegistration(feature) is not null => "Multiple Hotkeys May Be Active",
            _ => "Hotkey Update Failed"
        };
        NoticeRaised?.Invoke(new(result.Description, title, result.Status != HotkeyChangeStatus.Saved));
    }

    internal void ShowBackupResult(FullBackupResult result, FullBackupInfo backupInfo)
    {
        var resultMessage = result.Status == FullBackupStatus.Created && backupInfo.IsValid
            ? BuildBackupStatusText(backupInfo)
            : result.Message;
        BackupStatus = string.IsNullOrWhiteSpace(result.Warning)
            ? resultMessage
            : $"{resultMessage} {result.Warning}";
        BackupStatusResource = result.Status switch
        {
            FullBackupStatus.Created => "NotedAccentBrush",
            FullBackupStatus.Skipped => "NotedSecondaryTextBrush",
            _ => "NotedDangerBrush"
        };
        LastBackupAttemptFailed = result.Status is
            FullBackupStatus.Blocked or FullBackupStatus.Failed;
    }

    internal void UpdateBackupInfo(FullBackupInfo info, bool updateStatusText = true)
    {
        CreateBackupText = info.Exists ? "Update Backup" : "Create Backup";
        CanRestoreBackup = CanViewBackup = CanExportBackup = info.IsValid;
        CanImportBackup = true;
        if (!updateStatusText) return;
        BackupStatus = !info.Exists ? "No full backup has been created yet."
            : !info.IsValid ? $"The existing backup needs attention: {info.Error}" : BuildBackupStatusText(info);
        BackupStatusResource = info.Exists && !info.IsValid ? "NotedDangerBrush" : "NotedSecondaryTextBrush";
    }
    private static string FormatDisplayName(string deviceName)
    {
        var name = deviceName.Replace(@"\\.\", string.Empty, StringComparison.OrdinalIgnoreCase);
        return name.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? $"Display {name["DISPLAY".Length..]}"
            : name;
    }


    private static string BuildBackupStatusText(FullBackupInfo backupInfo)
    {
        var backupDate = backupInfo.LastUpdatedUtc?.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt")
            ?? "unknown date";
        var fileLabel = backupInfo.FileCount == 1 ? "file" : "files";
        return $"Last backup: {backupDate} â€¢ {backupInfo.FileCount} {fileLabel}";
    }


    private sealed class SettingsCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
        internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
