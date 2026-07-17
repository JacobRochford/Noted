using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace Noted;
    

public partial class MainWindow : Window {
    // Services
    private readonly IAppSettingsService _settingsService;
    private readonly INoteFileService _fileService;
    private readonly INotepadProcessService _notepadService;
    private readonly IStartupService _startupService;
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _renameBannerTimer;
    private GlobalHotkeysService? _globalHotkeysService;
    private HotkeyRegistration? _mainHotkeyRegistration;
    private HotkeyRegistration? _additionalMainHotkeyRegistration;
    private HotkeyRegistration? _checklistHotkeyRegistration;
    private HotkeyRegistration? _dictionaryHotkeyRegistration;
    private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _trayIcon;

    // UI State
    private bool _isUpdatingSettingsView;
    private bool _runOnStartupDisplayedState;
    private bool _hideButtonHidesAll;
    private bool _cleanupCompleted;
    private bool _hotkeyInitializationInProgress;
    private bool _hotkeysInitialized;
    private bool _isNoActivateTemporarilyStripped;

    // Ghost mode state
    private const double DefaultPanelOpacity = 0.88;
    private const double DefaultGhostModeOpacity = 0.25;
    private const double MinimumPanelOpacity = 0.05;
    private bool _ghostModeEnabled;
    private double _ghostModeOpacity;
    private double _defaultOpacity;

    // Drag state
    private Point? _dragStart;
    private FrameworkElement? _dragElement;
    private bool _isDragging;
    private double _dragInitialLeft;
    private double _dragInitialTop;
    private const double _dragThreshold = 5.0;

    private static readonly string[] FallbackHotkeyModifiers =
        { "Alt+Shift", "Ctrl+Alt", "Win+Alt", "Ctrl+Shift" };

    private sealed record InitialHotkeyResult(
        HotkeyRegistration? Registration,
        bool UsedFallback,
        string? Warning);


    public MainWindow(
            IAppSettingsService settingsService,
            INoteFileService fileService,
            INotepadProcessService notepadService,
            IStartupService startupService
        )
    {
        InitializeComponent();

        _settingsService = settingsService;
        _fileService = fileService;
        _notepadService = notepadService;
        _startupService = startupService;

        _viewModel = new MainWindowViewModel(_fileService, _settingsService, action => Dispatcher.Invoke(action));
        DataContext = _viewModel;

        InitializeTrayIcon();
        InitializeSettings();

        _renameBannerTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(10)
        };
        _renameBannerTimer.Tick += RenameBannerTimer_Tick;

        InitializeEventHandlers();
        InitializeNotesView();
        InitializeHotkeyUI();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)Resources["TrayIcon"];
        _trayIcon.TrayRightMouseUp += TrayIcon_TrayRightMouseUp;
    }

    private void InitializeSettings()
    {
        _ghostModeEnabled = _settingsService.LoadGhostModeEnabled();
        _ghostModeOpacity = NormalizeOpacity(
            _settingsService.LoadGhostModeOpacity(),
            0.0,
            DefaultGhostModeOpacity);
        _defaultOpacity = NormalizeOpacity(
            _settingsService.LoadDefaultOpacity(),
            MinimumPanelOpacity,
            DefaultPanelOpacity);
        _hideButtonHidesAll = _settingsService.LoadHideButtonHidesAll();
    }

    private void InitializeEventHandlers()
    {
        OverlayButton.PreviewMouseLeftButtonDown += OverlayButton_MouseLeftButtonDown;
        OverlayButton.PreviewMouseMove += OverlayButton_MouseMove;
        OverlayButton.PreviewMouseLeftButtonUp += OverlayButton_MouseLeftButtonUp;
        NotesPanel.PreviewMouseLeftButtonDown += NotesPanel_PreviewMouseLeftButtonDown;
        NotesPanel.PreviewMouseMove += NotesPanel_PreviewMouseMove;
        NotesPanel.PreviewMouseLeftButtonUp += NotesPanel_PreviewMouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown += MainCanvas_MouseLeftButtonDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        KeyDown += MainWindow_KeyDown;
        NotesPanel.MouseEnter += NotesPanel_MouseEnter;
        NotesPanel.MouseLeave += NotesPanel_MouseLeave;
        GhostModeOpacitySlider.ValueChanged += GhostModeOpacitySlider_ValueChanged;
        DefaultOpacitySlider.ValueChanged += DefaultOpacitySlider_ValueChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.GotFocus += SearchBox_GotFocus;
        SearchBox.LostFocus += SearchBox_LostFocus;
        _viewModel.PropertyChanged += OnViewModelFilterTextChanged;
    }

    private void InitializeNotesView()
    {
        FileList.ItemsSource = _viewModel.Notes;
        _viewModel.NotesLoaded += OnNotesLoaded;
        _viewModel.LoadNotes();
        UpdateNotesDirectoryDisplay();
        UpdateSettingsView();
        SetSettingsViewVisible(false);
    }

    private void InitializeHotkeyUI()
    {
        HotkeyKeyCombo.ItemsSource = HotkeyConstants.ValidKeys;
        ModifierCtrl.Checked += (s, e) => UpdateHotkeyPreview();
        ModifierCtrl.Unchecked += (s, e) => UpdateHotkeyPreview();
        ModifierAlt.Checked += (s, e) => UpdateHotkeyPreview();
        ModifierAlt.Unchecked += (s, e) => UpdateHotkeyPreview();
        ModifierShift.Checked += (s, e) => UpdateHotkeyPreview();
        ModifierShift.Unchecked += (s, e) => UpdateHotkeyPreview();
        ModifierWin.Checked += (s, e) => UpdateHotkeyPreview();
        ModifierWin.Unchecked += (s, e) => UpdateHotkeyPreview();
        HotkeyKeyCombo.SelectionChanged += (s, e) => UpdateHotkeyPreview();
    }

    private void HeaderEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        EndHeaderEdit(true);
    }

    private void HeaderEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndHeaderEdit(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndHeaderEdit(false);
            e.Handled = true;
        }
    }
    private void HeaderText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            var currentHeaderText = _viewModel.EditableHeaderText;
            Dispatcher.InvokeAsync(() => BeginHeaderEdit(currentHeaderText), DispatcherPriority.Input);
        }
    }

    private void BeginHeaderEdit(string currentHeaderText)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.StripNoActivate(hwnd);
        Activate();
        WindowInterop.SetForegroundWindow(hwnd);

        _viewModel.IsHeaderEditing = true;
        HeaderText.Visibility = Visibility.Collapsed;
        HeaderTextEdit.Visibility = Visibility.Visible;
        _viewModel.HeaderTextEdit = currentHeaderText;

        HeaderTextEdit.Focus();
        FocusManager.SetFocusedElement(this, HeaderTextEdit);
        Keyboard.Focus(HeaderTextEdit);
        HeaderTextEdit.SelectAll();
    }

    private void EndHeaderEdit(bool save)
    {
        if (!_viewModel.IsHeaderEditing) return;
        _viewModel.IsHeaderEditing = false;
        HeaderText.Visibility = Visibility.Visible;
        HeaderTextEdit.Visibility = Visibility.Collapsed;

        var hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.RestoreNoActivate(hwnd);

        if (save)
        {
            var newHeader = HeaderTextEdit.Text.Trim();
            if (string.IsNullOrWhiteSpace(newHeader))
            {
                _settingsService.SaveCustomHeader("");
            }
            else
            {
                _settingsService.SaveCustomHeader(newHeader);
            }
            _viewModel.SetCustomHeader(newHeader);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e) {
        // Set HWND properties before the window is shown so Windows never classifies
        // this overlay as a fullscreen foreground app (which would trigger Do Not Disturb).
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = WindowInterop.GetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE);
        WindowInterop.SetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE,
            exStyle | WindowInterop.WS_EX_TOOLWINDOW | WindowInterop.WS_EX_NOACTIVATE);
        WindowInterop.SetWindowPos(hwnd, WindowInterop.HWND_TOPMOST, 0, 0, 0, 0,
            WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE);
        // NonRudeHWND tells Windows explicitly this is not a rude fullscreen app.
        WindowInterop.SetProp(hwnd, "NonRudeHWND", new IntPtr(1));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        var newNoteMode = _settingsService.LoadNewNoteMode();
        QuickNoteButton.Visibility = newNoteMode == NewNoteMode.Both
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Span the overlay across the full virtual desktop so elements can be dragged to any monitor.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Move the Notes button just above the clock (system tray) on startup
        const double taskbarHeight = 60;
        const double marginFromTaskbar = 8;
        Canvas.SetBottom(OverlayButton, taskbarHeight + marginFromTaskbar);

        InitializeGlobalHotkeys();
        
        if (_ghostModeEnabled && NotesPanel.Visibility == Visibility.Visible)
            AnimatePanelOpacity(_ghostModeOpacity, allowFullyTransparent: true);
        else
            AnimatePanelOpacity(_defaultOpacity);
    }

    private void InitializeGlobalHotkeys()
    {
        if (_cleanupCompleted
            || _hotkeysInitialized
            || _hotkeyInitializationInProgress)
            return;

        _hotkeyInitializationInProgress = true;
        var warnings = new List<string>();

        try
        {
            _globalHotkeysService ??= new GlobalHotkeysService(this);

            if (_mainHotkeyRegistration is null)
            {
                var (modifiers, key) = _settingsService.LoadGlobalHotkey();
                var result = RegisterWithFallback(
                    "Main",
                    modifiers,
                    key,
                    "Ctrl+Shift",
                    "Space",
                    OnGlobalHotkeyPressed,
                    _settingsService.SaveGlobalHotkey);
                _mainHotkeyRegistration = result.Registration;
                if (result.Warning is not null)
                    warnings.Add(result.Warning);
            }

            if (_checklistHotkeyRegistration is null)
            {
                var (modifiers, key) = _settingsService.LoadChecklistHotkey();
                var result = RegisterWithFallback(
                    "Checklist",
                    modifiers,
                    key,
                    "Alt",
                    "C",
                    OnChecklistHotkeyPressed,
                    _settingsService.SaveChecklistHotkey);
                _checklistHotkeyRegistration = result.Registration;
                if (result.Warning is not null)
                    warnings.Add(result.Warning);
            }

            if (_dictionaryHotkeyRegistration is null)
            {
                var (modifiers, key) = _settingsService.LoadDictionaryHotkey();
                var result = RegisterWithFallback(
                    "Dictionary",
                    modifiers,
                    key,
                    "Alt",
                    "D",
                    OnDictionaryHotkeyPressed,
                    _settingsService.SaveDictionaryHotkey);
                _dictionaryHotkeyRegistration = result.Registration;
                if (result.Warning is not null)
                    warnings.Add(result.Warning);
            }

            _hotkeysInitialized = true;
            SynchronizeMainHotkeyControls(restoreEditorToActive: true);
        }
        catch (Exception exception)
        {
            warnings.Add(
                $"Hotkey initialization stopped before all features were processed: {exception.Message}\n" +
                "Registrations already owned by Noted were retained; initialization may be retried without replacing the service.");
        }
        finally
        {
            _hotkeyInitializationInProgress = false;
        }

        if (warnings.Count > 0)
        {
            MessageBox.Show(
                string.Join("\n\n", warnings),
                "Hotkey Registration Notice",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private InitialHotkeyResult RegisterWithFallback(
        string featureName,
        string requestedModifiers,
        string requestedKey,
        string defaultFallbackModifiers,
        string defaultFallbackKey,
        Action callback,
        Action<string, string> saveHotkey)
    {
        if (_globalHotkeysService is null)
        {
            return new InitialHotkeyResult(
                null,
                false,
                $"{featureName} hotkey could not be registered because the hotkey service is unavailable.");
        }

        var candidates = new List<(string Modifiers, string Key)>
        {
            (requestedModifiers, requestedKey),
            (defaultFallbackModifiers, defaultFallbackKey),
            (FallbackHotkeyModifiers[0], requestedKey),
            (FallbackHotkeyModifiers[1], requestedKey),
            (FallbackHotkeyModifiers[2], requestedKey),
            (FallbackHotkeyModifiers[3], requestedKey),
            (FallbackHotkeyModifiers[0], defaultFallbackKey),
            (FallbackHotkeyModifiers[1], defaultFallbackKey),
            (FallbackHotkeyModifiers[2], defaultFallbackKey),
            (FallbackHotkeyModifiers[3], defaultFallbackKey)
        };

        var attemptedCombinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var requestedValidation = _globalHotkeysService.Validate(
            requestedModifiers,
            requestedKey);
        var requestedDisplay = requestedValidation.Combination
            ?? $"{requestedModifiers}+{requestedKey}";

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var validation = _globalHotkeysService.Validate(candidate.Modifiers, candidate.Key);
            if (!validation.Success
                || validation.Modifiers is null
                || validation.Key is null
                || validation.Combination is null)
            {
                failures.Add(validation.Description);
                continue;
            }

            if (!attemptedCombinations.Add(validation.Combination))
                continue;

            var registrationResult = _globalHotkeysService.Register(
                featureName,
                validation.Modifiers,
                validation.Key,
                callback);
            if (!registrationResult.Success || registrationResult.Registration is null)
            {
                failures.Add(registrationResult.Description);
                continue;
            }

            var registration = registrationResult.Registration;
            if (index == 0)
                return new InitialHotkeyResult(registration, false, null);

            string? persistenceWarning = null;
            try
            {
                saveHotkey(registration.Modifiers, registration.Key);
            }
            catch (Exception exception)
            {
                persistenceWarning =
                    $" The fallback is active for this session, but saving it failed: {exception.Message}";
            }

            return new InitialHotkeyResult(
                registration,
                true,
                $"{featureName} hotkey {requestedDisplay} was unavailable. " +
                $"Active fallback: {registration.Combination}.{persistenceWarning}");
        }

        var failureSummary = string.Join(
            " ",
            failures.Where(failure => !string.IsNullOrWhiteSpace(failure)).Distinct());
        return new InitialHotkeyResult(
            null,
            false,
            $"{featureName} hotkey {requestedModifiers}+{requestedKey} could not be registered. " +
            $"No fallback is active. {failureSummary}".TrimEnd());
    }

    private void OnGlobalHotkeyPressed()
    {
        WindowManager.ToggleWorkspaceVisibility();
    }

    private void OnChecklistHotkeyPressed()
    {
        WindowManager.ToggleChecklist();
    }

    private void OnDictionaryHotkeyPressed()
    {
        WindowManager.ToggleDictionary();
    }

    // Restore the tracked selection after every reload of notes.
    private void OnNotesLoaded(object? sender, EventArgs e) {
        UpdateNotesDirectoryDisplay();
        if (_viewModel.SelectedFileName is not null)
            FileList.SelectedItem = _viewModel.FindNote(_viewModel.SelectedFileName);
    }


    private void UpdateNotesDirectoryDisplay() {
        SettingsNotesDirectoryText.Text = _fileService.NotesDirectory;
        SettingsButton.ToolTip = SettingsView.Visibility == Visibility.Visible
            ? "Return to notes"
            : "Open settings";
        UpdateShowHideNotesDirectoryButton();
        // Visibility is handled by binding.
    }

    private void UpdateShowHideNotesDirectoryButton()
    {
        if (ShowHideNotesDirectoryButton != null)
            ShowHideNotesDirectoryButton.Content = _viewModel.ShowNotesDirectory ? "Hide Folder" : "Show Folder";
    }

    private void ShowHideNotesDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowNotesDirectory = !_viewModel.ShowNotesDirectory;
        UpdateShowHideNotesDirectoryButton();
    }

    private void PinNoteButton_Click(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement element && element.DataContext is NoteItem note) {
            _viewModel.TogglePin(note);
        }
    }

    private void SetSettingsViewVisible(bool setVisible) {
        NotesListView.Visibility = setVisible ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = setVisible ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Content = setVisible ? "Notes" : "Settings";
        HeaderText.Visibility = setVisible ? Visibility.Collapsed : Visibility.Visible;
        _viewModel.IsSettingsVisible = setVisible;
        UpdateNotesDirectoryDisplay();
        // Restore correct opacity when closing settings (slider may have previewed a value)
        if (!setVisible)
            AnimatePanelOpacity(
                _ghostModeEnabled ? _ghostModeOpacity : _defaultOpacity,
                allowFullyTransparent: _ghostModeEnabled);
    }

    private void UpdateSettingsView() {
        _isUpdatingSettingsView = true;
        try {
            var newNoteMode = _settingsService.LoadNewNoteMode();
            NewNotePromptOption.IsChecked = newNoteMode == NewNoteMode.Prompt;
            NewNoteQuickOption.IsChecked = newNoteMode == NewNoteMode.Quick;
            NewNoteBothOption.IsChecked = newNoteMode == NewNoteMode.Both;
            QuickNoteButton.Visibility = newNoteMode == NewNoteMode.Both
                ? Visibility.Visible
                : Visibility.Collapsed;

            var timestampPlacement = _settingsService.LoadTimestampPlacement();
            TimestampNoneOption.IsChecked = timestampPlacement == NoteTimestampPlacement.None;
            TimestampTopOption.IsChecked = timestampPlacement == NoteTimestampPlacement.Top;
            TimestampBottomOption.IsChecked = timestampPlacement == NoteTimestampPlacement.Bottom;
            SettingsNotesDirectoryText.Text = _fileService.NotesDirectory;
            var showModified = _settingsService.LoadShowModifiedSubtitle();
            ShowModifiedSubtitleOption.IsChecked = showModified;
            _viewModel.ShowModifiedSubtitle = showModified;

            // Load hotkey settings
            var (modifiers, key) = _settingsService.LoadGlobalHotkey();
            if (_mainHotkeyRegistration is not null
                && _additionalMainHotkeyRegistration is null)
            {
                SetHotkeyEditor(
                    _mainHotkeyRegistration.Modifiers,
                    _mainHotkeyRegistration.Key);
            }
            else
            {
                SetHotkeyEditor(modifiers, key);
            }

            UpdateCurrentHotkeyDisplay();
            _ghostModeEnabled = _settingsService.LoadGhostModeEnabled();
            _ghostModeOpacity = NormalizeOpacity(
                _settingsService.LoadGhostModeOpacity(),
                0.0,
                DefaultGhostModeOpacity);
            _defaultOpacity = NormalizeOpacity(
                _settingsService.LoadDefaultOpacity(),
                MinimumPanelOpacity,
                DefaultPanelOpacity);
            GhostModeOption.IsChecked = _ghostModeEnabled;
            GhostModeOpacitySlider.Value = _ghostModeOpacity * 100;
            UpdateGhostModeOpacityLabel();
            DefaultOpacitySlider.Value = _defaultOpacity * 100;
            UpdateDefaultOpacityLabel();
            var folderNavigationMode = _settingsService.LoadFolderNavigationMode()
                == FolderNavigationMode.Expand
                ? FolderNavigationMode.Expand
                : FolderNavigationMode.DrillDown;
            FolderNavDrillDownOption.IsChecked = folderNavigationMode == FolderNavigationMode.DrillDown;
            FolderNavExpandOption.IsChecked = folderNavigationMode == FolderNavigationMode.Expand;
            _viewModel.FolderNavigationMode = folderNavigationMode;
            _runOnStartupDisplayedState = _startupService.IsRunOnStartupEnabled;
            RunOnStartupOption.IsChecked = _runOnStartupDisplayedState;
            _hideButtonHidesAll = _settingsService.LoadHideButtonHidesAll();
            HideButtonHidesAllOption.IsChecked = _hideButtonHidesAll;
        } finally {
            _isUpdatingSettingsView = false;
        }
    }

    private void SetHotkeyEditor(string modifiers, string key)
    {
        HotkeyKeyCombo.SelectedItem = key;

        var modifierList = modifiers
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ModifierCtrl.IsChecked = modifierList.Contains("Ctrl");
        ModifierAlt.IsChecked = modifierList.Contains("Alt");
        ModifierShift.IsChecked = modifierList.Contains("Shift");
        ModifierWin.IsChecked = modifierList.Contains("Win");

        UpdateHotkeyPreview();
    }

    private void SynchronizeMainHotkeyControls(bool restoreEditorToActive)
    {
        if (restoreEditorToActive
            && _mainHotkeyRegistration is not null
            && _additionalMainHotkeyRegistration is null)
        {
            SetHotkeyEditor(
                _mainHotkeyRegistration.Modifiers,
                _mainHotkeyRegistration.Key);
        }

        UpdateCurrentHotkeyDisplay();
    }

    private void UpdateCurrentHotkeyDisplay()
    {
        if (_mainHotkeyRegistration is null)
        {
            CurrentHotkeyDisplay.Text = "Not registered";
            return;
        }

        if (_additionalMainHotkeyRegistration is not null)
        {
            CurrentHotkeyDisplay.Text =
                $"Multiple active: {_mainHotkeyRegistration.Combination} and " +
                _additionalMainHotkeyRegistration.Combination;
            return;
        }

        CurrentHotkeyDisplay.Text = _mainHotkeyRegistration.Combination;
    }

    private void UpdateHotkeyPreview()
    {
        var selectedModifiers = new List<string>();
        if (ModifierCtrl.IsChecked == true) selectedModifiers.Add("Ctrl");
        if (ModifierAlt.IsChecked == true) selectedModifiers.Add("Alt");
        if (ModifierShift.IsChecked == true) selectedModifiers.Add("Shift");
        if (ModifierWin.IsChecked == true) selectedModifiers.Add("Win");

        var selectedKey = HotkeyKeyCombo.SelectedItem as string;
        var builtHotkey = HotkeyConstants.BuildHotkey(selectedModifiers, selectedKey);

        HotkeyPreview.Text = builtHotkey ?? "(Select modifiers and key)";
    }

    private void ApplyHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedModifiers = new List<string>();
        if (ModifierCtrl.IsChecked == true) selectedModifiers.Add("Ctrl");
        if (ModifierAlt.IsChecked == true) selectedModifiers.Add("Alt");
        if (ModifierShift.IsChecked == true) selectedModifiers.Add("Shift");
        if (ModifierWin.IsChecked == true) selectedModifiers.Add("Win");

        var selectedKey = HotkeyKeyCombo.SelectedItem as string;
        var builtHotkey = HotkeyConstants.BuildHotkey(selectedModifiers, selectedKey);

        if (selectedModifiers.Count == 0
            || string.IsNullOrWhiteSpace(selectedKey)
            || string.IsNullOrEmpty(builtHotkey))
        {
            MessageBox.Show(
                "Please select at least one modifier and a key.",
                "Invalid Hotkey Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var modifiers = string.Join("+", selectedModifiers);
        if (_globalHotkeysService is null)
        {
            MessageBox.Show(
                "The global hotkey service is unavailable. Restart Noted and try again.",
                "Hotkey Update Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateCurrentHotkeyDisplay();
            return;
        }

        var validation = _globalHotkeysService.Validate(modifiers, selectedKey);
        if (!validation.Success || validation.Modifiers is null || validation.Key is null)
        {
            SynchronizeMainHotkeyControls(restoreEditorToActive: true);
            MessageBox.Show(
                validation.Description,
                "Invalid Hotkey Selection",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_additionalMainHotkeyRegistration is not null)
        {
            UpdateCurrentHotkeyDisplay();
            MessageBox.Show(
                "Multiple Main hotkeys may still be active after an earlier rollback failure. " +
                "Restart Noted before attempting another replacement.",
                "Hotkey State Is Ambiguous",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var replacement = _globalHotkeysService.Replace(
            "Main",
            _mainHotkeyRegistration,
            validation.Modifiers,
            validation.Key,
            OnGlobalHotkeyPressed);

        _mainHotkeyRegistration = replacement.ActiveRegistration;
        _additionalMainHotkeyRegistration = replacement.AdditionalActiveRegistration;

        if (!replacement.Success)
        {
            SynchronizeMainHotkeyControls(
                restoreEditorToActive: !replacement.HasDualActiveRegistrations
                    && _mainHotkeyRegistration is not null);
            MessageBox.Show(
                replacement.Description,
                replacement.HasDualActiveRegistrations
                    ? "Multiple Hotkeys May Be Active"
                    : "Hotkey Update Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SynchronizeMainHotkeyControls(restoreEditorToActive: true);
        if (replacement.NoChange || _mainHotkeyRegistration is null)
            return;

        try
        {
            _settingsService.SaveGlobalHotkey(
                _mainHotkeyRegistration.Modifiers,
                _mainHotkeyRegistration.Key);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The hotkey is active as {_mainHotkeyRegistration.Combination}, " +
                $"but the setting could not be saved: {exception.Message}",
                "Hotkey Active but Not Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            $"Global hotkey updated to: {_mainHotkeyRegistration.Combination}",
            "Hotkey Updated",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
    private void ShowModifiedSubtitleOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSettingsView) return;
        var isChecked = ShowModifiedSubtitleOption.IsChecked ?? true;
        _settingsService.SaveShowModifiedSubtitle(isChecked);
        _viewModel.ShowModifiedSubtitle = isChecked;
    }

    private void RunOnStartupOption_Changed(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView) return;

        var requestedState = RunOnStartupOption.IsChecked == true;
        var previousDisplayedState = _runOnStartupDisplayedState;
        var result = _startupService.SetRunOnStartup(requestedState);
        var displayedState = result.ActualEnabled ?? previousDisplayedState;

        _isUpdatingSettingsView = true;
        try {
            RunOnStartupOption.IsChecked = displayedState;
            _runOnStartupDisplayedState = displayedState;
        } finally {
            _isUpdatingSettingsView = false;
        }

        if (!result.Success || result.ActualEnabled is null || result.ActualEnabled != requestedState) {
            MessageBox.Show(
                result.Error ?? "Windows did not apply the requested startup setting.",
                "Startup Setting Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void HideButtonHidesAllOption_Changed(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView) return;
        _hideButtonHidesAll = HideButtonHidesAllOption.IsChecked == true;
        _settingsService.SaveHideButtonHidesAll(_hideButtonHidesAll);
    }

    private void FolderNavOption_Checked(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView) return;

        var folderNavigationMode = sender switch {
            RadioButton { Name: nameof(FolderNavDrillDownOption) } => FolderNavigationMode.DrillDown,
            RadioButton { Name: nameof(FolderNavExpandOption) } => FolderNavigationMode.Expand,
            _ => (FolderNavigationMode?)null
        };

        if (!folderNavigationMode.HasValue)
            return;

        _settingsService.SaveFolderNavigationMode(folderNavigationMode.Value);
        _viewModel.FolderNavigationMode = folderNavigationMode.Value;
    }

    private void GhostModeOption_Changed(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView) return;
        _ghostModeEnabled = GhostModeOption.IsChecked ?? false;
        _settingsService.SaveGhostModeEnabled(_ghostModeEnabled);
        if (!_ghostModeEnabled)
            AnimatePanelOpacity(_defaultOpacity);
        else if (NotesPanel.Visibility == Visibility.Visible && !NotesPanel.IsMouseOver)
            AnimatePanelOpacity(_ghostModeOpacity, allowFullyTransparent: true);
    }

    private void GhostModeOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (_isUpdatingSettingsView) return;
        _ghostModeOpacity = NormalizeOpacity(
            e.NewValue / 100.0,
            0.0,
            DefaultGhostModeOpacity);
        CorrectOpacitySliderValue(GhostModeOpacitySlider, _ghostModeOpacity);
        _settingsService.SaveGhostModeOpacity(_ghostModeOpacity);
        UpdateGhostModeOpacityLabel();
        if (NotesPanel.Visibility == Visibility.Visible)
            AnimatePanelOpacity(_ghostModeOpacity, allowFullyTransparent: true);
    }

    private void UpdateGhostModeOpacityLabel() {
        GhostModeOpacityLabel.Text = $"{(int)GhostModeOpacitySlider.Value}%";
    }

    private void DefaultOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (_isUpdatingSettingsView) return;
        _defaultOpacity = NormalizeOpacity(
            e.NewValue / 100.0,
            MinimumPanelOpacity,
            DefaultPanelOpacity);
        CorrectOpacitySliderValue(DefaultOpacitySlider, _defaultOpacity);
        _settingsService.SaveDefaultOpacity(_defaultOpacity);
        UpdateDefaultOpacityLabel();
        if (NotesPanel.Visibility == Visibility.Visible && (!_ghostModeEnabled || NotesPanel.IsMouseOver))
            AnimatePanelOpacity(_defaultOpacity);
    }

    private void UpdateDefaultOpacityLabel() {
        DefaultOpacityLabel.Text = $"{(int)DefaultOpacitySlider.Value}%";
    }

    private void CorrectOpacitySliderValue(Slider slider, double normalizedOpacity) {
        var displayValue = normalizedOpacity * 100.0;
        if (Math.Abs(slider.Value - displayValue) < 0.0001)
            return;

        _isUpdatingSettingsView = true;
        try {
            slider.Value = displayValue;
        } finally {
            _isUpdatingSettingsView = false;
        }
    }

    private static double NormalizeOpacity(double value, double minimum, double fallback) {
        if (!double.IsFinite(value))
            return fallback;

        return Math.Clamp(value, minimum, 1.0);
    }

    private void NotesPanel_MouseEnter(object sender, MouseEventArgs e) {
        if (_ghostModeEnabled)
            AnimatePanelOpacity(_defaultOpacity);
    }

    private void NotesPanel_MouseLeave(object sender, MouseEventArgs e) {
        if (_ghostModeEnabled)
            AnimatePanelOpacity(_ghostModeOpacity, allowFullyTransparent: true);
    }

    private void AnimatePanelOpacity(double targetOpacity, bool allowFullyTransparent = false) {
        var normalizedTargetOpacity = NormalizeOpacity(
            targetOpacity,
            allowFullyTransparent ? 0.0 : MinimumPanelOpacity,
            allowFullyTransparent ? DefaultGhostModeOpacity : DefaultPanelOpacity);
        var anim = new DoubleAnimation(
            NotesPanel.Opacity,
            normalizedTargetOpacity,
            new Duration(TimeSpan.FromMilliseconds(200)));
        NotesPanel.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    internal bool IsNotesPanelVisible => NotesPanel.Visibility == Visibility.Visible;

    internal void ShowNotesPanel() {
        NotesPanel.Visibility = Visibility.Visible;
        if (_ghostModeEnabled)
            AnimatePanelOpacity(_ghostModeOpacity, allowFullyTransparent: true);
        else
            AnimatePanelOpacity(_defaultOpacity);
        if (_notepadService.IsRunning)
            _notepadService.Restore();

        // force focus for overlay
        var hwnd = TemporarilyAllowWindowActivation();
        try {
            if (!WindowInterop.SetForegroundWindow(hwnd)) {
                RestoreNoActivateAfterInteraction();
                return;
            }

            // focus list after layout
            Dispatcher.InvokeAsync(() => {
                try {
                    FileList.Focus();
                } finally {
                    RestoreNoActivateAfterInteraction();
                }
            }, DispatcherPriority.Input);
        } catch {
            RestoreNoActivateAfterInteraction();
            throw;
        }
    }

    internal void HideNotesPanel() => HideNotesPanelAndMinimizeNotepad();

    private void HideNotesPanelAndMinimizeNotepad() {
        try {
            _viewModel.ClearFilter();
        } finally {
            RestoreNoActivateAfterInteraction();
            NotesPanel.Visibility = Visibility.Collapsed;
            _notepadService.Minimize();
        }
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e) {
        WindowManager.ToggleWorkspaceVisibility();
    }

    private void MinimizeNotesButton_Click(object sender, RoutedEventArgs e) {
        if (_hideButtonHidesAll)
            WindowManager.HideAll();
        else
            HideNotesPanelAndMinimizeNotepad();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) {
        var showSettings = SettingsView.Visibility != Visibility.Visible;
        if (showSettings)
        {
            UpdateSettingsView();
        }
        else
        {
            // Always refresh notes list when returning from settings
            var showModified = _settingsService.LoadShowModifiedSubtitle();
            _viewModel.ShowModifiedSubtitle = showModified;
            _viewModel.LoadNotes();
        }
        SetSettingsViewVisible(showSettings);
    }

    private void BackToNotesButton_Click(object sender, RoutedEventArgs e) {
        // Refresh notes list to reflect any settings changes (e.g., Show Modified Subtitle)
        _viewModel.LoadNotes();
        SetSettingsViewVisible(false);
    }

    private void ChecklistButton_Click(object sender, RoutedEventArgs e) {
        WindowManager.ToggleChecklist();
    }

    private void DictionaryButton_Click(object sender, RoutedEventArgs e) {
        WindowManager.ToggleDictionary();
    }

    private void ScratchpadButton_Click(object sender, RoutedEventArgs e) {
        WindowManager.ToggleScratchpad();
    }

    private void CopyNoteName_Click(object sender, RoutedEventArgs e)
    {
        NoteItem? note = null;
        if (sender is FrameworkElement element && element.DataContext is NoteItem itemNote)
        {
            note = itemNote;
            FileList.SelectedItem = itemNote;
        }
        else
        {
            note = FileList.SelectedItem as NoteItem;
        }
        if (note is null) return;
        try
        {
            Clipboard.SetText(note.DisplayName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy note name:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void NewNoteButton_Click(object sender, RoutedEventArgs e) {
        try {
            var filename = CreateNewNoteFromCurrentSettings();
            if (filename is null)
                return;

            _viewModel.LoadNotes(filename);
            // OnNotesLoaded fires synchronously above, so selection is already set.
            _notepadService.Open(Path.Combine(_fileService.CurrentDirectory, filename));
        } catch (Exception ex) {
            MessageBox.Show($"Failed to create note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QuickNoteButton_Click(object sender, RoutedEventArgs e) {
        try {
            var filename = _fileService.CreateNote();
            _viewModel.LoadNotes(filename);
            _notepadService.Open(Path.Combine(_fileService.CurrentDirectory, filename));
        } catch (Exception ex) {
            MessageBox.Show($"Failed to create quick note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? CreateNewNoteFromCurrentSettings() {
        var mode = _settingsService.LoadNewNoteMode();
        if (mode == NewNoteMode.Quick)
            return _fileService.CreateNote();

        var attemptedName = string.Empty;
        while (true) {
            var dialog = new NewNoteNameDialog(attemptedName) {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return null;

            try {
                return _fileService.CreateNote(dialog.NoteName);
            } catch (InvalidOperationException ex) {
                attemptedName = dialog.NoteName;
                MessageBox.Show(ex.Message,
                    "New Note Name", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void NewNoteMode_Changed(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView)
            return;

        NewNoteMode? newNoteMode = sender switch {
            RadioButton { Name: nameof(NewNotePromptOption) } => NewNoteMode.Prompt,
            RadioButton { Name: nameof(NewNoteQuickOption) } => NewNoteMode.Quick,
            RadioButton { Name: nameof(NewNoteBothOption) } => NewNoteMode.Both,
            _ => null
        };

        if (!newNoteMode.HasValue)
            return;

        _settingsService.SaveNewNoteMode(newNoteMode.Value);
        QuickNoteButton.Visibility = newNoteMode == NewNoteMode.Both
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TimestampPlacementOption_Checked(object sender, RoutedEventArgs e) {
        if (_isUpdatingSettingsView)
            return;

        var timestampPlacement = sender switch {
            RadioButton { Name: nameof(TimestampTopOption) } => NoteTimestampPlacement.Top,
            RadioButton { Name: nameof(TimestampBottomOption) } => NoteTimestampPlacement.Bottom,
            _ => NoteTimestampPlacement.None
        };

        _settingsService.SaveTimestampPlacement(timestampPlacement);
    }

    private void DeleteNoteButton_Click(object sender, RoutedEventArgs e) {
        NoteItem? note = null;

        if (sender is FrameworkElement element && element.DataContext is NoteItem itemNote) {
            note = itemNote;
            FileList.SelectedItem = itemNote;
        } else {
            note = FileList.SelectedItem as NoteItem;
        }

        if (note is null) return;

        if (note.IsFolder) {
            var message = $"Delete folder '{note.DisplayName}' and all its contents?";
            if (MessageBox.Show(message, "Delete Folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var (success, error) = _fileService.DeleteFolder(note.FileName);
            if (!success)
                MessageBox.Show(error ?? "Failed to delete folder.", "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            _viewModel.LoadNotes();
        } else {
            try {
                var containingDirectory = note.FullPath is null
                    ? null
                    : Path.GetDirectoryName(note.FullPath);
                _fileService.DeleteNote(note.FileName, containingDirectory);
                // FileSystemWatcher triggers a debounced reload automatically.
            } catch (Exception ex) {
                MessageBox.Show($"Failed to delete note:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note)
            _viewModel.SelectedFileName = note.FileName;
    }

    private void OpenSelectedNote() {
        if (FileList.SelectedItem is not NoteItem note) return;

        // FullPath is set for child notes in Expand mode; drill-down mode uses the current folder.
        var filePath = note.FullPath ?? Path.Combine(_fileService.CurrentDirectory, note.FileName);
        _viewModel.SelectedFileName = note.FileName;

        if (_notepadService.Open(filePath)) {
            _notepadService.Restore();
            return;
        }

        if (_notepadService.IsRunning)
            _notepadService.Restore();
    }

    private void OpenSelectedNote_Click(object sender, RoutedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note && note.IsFolder)
            _viewModel.NavigateTo(note.FileName);
        else
            OpenSelectedNote();
    }

    private void NavigateUpButton_Click(object sender, RoutedEventArgs e) {
        _viewModel.NavigateUp();
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e) {
        if (_viewModel.IsSettingsVisible)
            return;

        if (e.ChangedButton == MouseButton.XButton1) {
            if (_viewModel.CanNavigateBack) {
                _viewModel.NavigateBack();
                e.Handled = true;
            }
            return;
        }

        if (e.ChangedButton == MouseButton.XButton2 && _viewModel.CanNavigateForward) {
            _viewModel.NavigateForward();
            e.Handled = true;
        }
    }

    private void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e) {
        if (FileList.SelectedItem is NoteItem folder && folder.IsFolder)
            _viewModel.NavigateTo(folder.FileName);
    }

    private void NewFolderMenuItem_Click(object sender, RoutedEventArgs e) {
        var dialog = new NewNoteNameDialog("") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var folderName = dialog.NoteName;
        if (string.IsNullOrWhiteSpace(folderName)) return;

        var (success, error) = _fileService.CreateFolder(folderName);
        if (!success) {
            MessageBox.Show(error ?? "Failed to create folder.", "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _viewModel.LoadNotes();
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e) {
        var note = FileList.SelectedItem as NoteItem;
        var isFolder = note?.IsFolder == true;
        var hasSelection = note is not null;
        OpenNoteMenuItem.Visibility = !isFolder && hasSelection ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderMenuItem.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        RenameMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CopyNameMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DeleteSeparator.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        DeleteMenuItem.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChangeFolderButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFolderDialog {
            Title = "Choose Notes Folder",
            InitialDirectory = _fileService.NotesDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        try {
            if (!EnsureCurrentNoteCanClose("finish with the currently open note before changing folders"))
                return;

            var changed = _fileService.ChangeNotesDirectory(dialog.FolderName);
            _viewModel.SelectedFileName = null;
            if (changed) {
                _viewModel.ClearFilter();
                _viewModel.LoadNotes();
            }

            UpdateNotesDirectoryDisplay();
            UpdateSettingsView();
        } catch (Exception ex) {
            MessageBox.Show($"Failed to change notes folder:\n{ex.Message}",
                "Folder Change Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (NotesPanel.Visibility == Visibility.Visible &&
            !NotesPanel.IsMouseOver && !OverlayButton.IsMouseOver) {
            HideNotesPanelAndMinimizeNotepad();
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && NotesPanel.Visibility == Visibility.Visible) {
            HideNotesPanelAndMinimizeNotepad();
            e.Handled = true;
        }
    }

    #region Note Renaming
    private void RenameNote_Click(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement element && element.DataContext is NoteItem itemNote) {
            FileList.SelectedItem = itemNote;
            StartRenaming(itemNote);
            return;
        }

        if (FileList.SelectedItem is NoteItem selectedNote)
            StartRenaming(selectedNote);
    }

    private void ListBoxItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (IsWithinNoteRowActionControl(e.OriginalSource as DependencyObject))
            return;

        if (sender is ListBoxItem item && item.DataContext is NoteItem note) {
            FileList.SelectedItem = note;
            if (note.IsFolder) {
                if (_viewModel.IsExpandMode)
                    _viewModel.ToggleFolderExpansion(note);
                else
                    _viewModel.NavigateTo(note.FileName);
            } else {
                OpenSelectedNote();
            }
            e.Handled = true;
        }
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter && FileList.SelectedItem is NoteItem enterNote) {
            if (enterNote.IsFolder) {
                if (_viewModel.IsExpandMode)
                    _viewModel.ToggleFolderExpansion(enterNote);
                else
                    _viewModel.NavigateTo(enterNote.FileName);
            } else {
                OpenSelectedNote();
            }
            e.Handled = true;
        } else if (e.Key == Key.F2 && FileList.SelectedItem is NoteItem note) {
            StartRenaming(note);
            e.Handled = true;
        } else if (e.Key == Key.Delete && FileList.SelectedItem is NoteItem) {
            DeleteNoteButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void StartRenaming(NoteItem note) {
        note.EditableName = Path.GetFileNameWithoutExtension(note.FileName);
        note.IsEditing = true;
        // Focus the textbox after the UI updates
        Dispatcher.InvokeAsync(() => {
            var listBoxItem = FileList.ItemContainerGenerator.ContainerFromItem(note) as ListBoxItem;
            if (listBoxItem != null) {
                var textBox = FindVisualChild<TextBox>(listBoxItem);
                textBox?.Focus();
                textBox?.SelectAll();
            }
        });
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key is not (Key.Return or Key.Escape))
            return;

        e.Handled = true;
        if (sender is not TextBox textBox)
            return;

        CompleteRename(textBox, commitChanges: e.Key == Key.Return);
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) {
        if (sender is TextBox textBox)
            CompleteRename(textBox, commitChanges: true);
    }

    private void CompleteRename(TextBox textBox, bool commitChanges) {
        if (textBox.DataContext is not NoteItem note || !note.IsEditing)
            return;

        var originalDisplayName = Path.GetFileNameWithoutExtension(note.FileName);
        var requestedEditedName = textBox.Text.Trim();
        var isFolder = note.IsFolder;

        note.IsEditing = false;
        note.EditableName = originalDisplayName;

        if (!commitChanges
            || string.IsNullOrWhiteSpace(requestedEditedName)
            || string.Equals(requestedEditedName, originalDisplayName, StringComparison.Ordinal))
            return;

        if (isFolder)
            CommitFolderRename(note, requestedEditedName);
        else
            CommitNoteRename(note, requestedEditedName);
    }

    private void CommitFolderRename(NoteItem folder, string newName) {
        var (success, error) = _fileService.RenameFolder(folder.FileName, newName);
        if (!success)
            MessageBox.Show(error ?? "Rename failed.", "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        _viewModel.LoadNotes();
    }

    private void CommitNoteRename(NoteItem note, string newDisplayName) {
        var oldFileName = note.FileName;
        var containingDirectory = note.FullPath is null
            ? _fileService.CurrentDirectory
            : Path.GetDirectoryName(note.FullPath) ?? _fileService.CurrentDirectory;
        var oldFilePath = Path.Combine(containingDirectory, oldFileName);
        var wasNoteOpen = _notepadService.IsFileOpen(oldFilePath);
        var (success, newFileName, error) = _fileService.RenameNote(
            note.FileName,
            newDisplayName,
            containingDirectory);
        if (!success) {
            if (error is not null)
                MessageBox.Show(error, "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _viewModel.LoadNotes(newFileName ?? note.FileName);
        if (!wasNoteOpen || newFileName is null)
            return;

        ShowRenameNotice(_viewModel.FindNote(newFileName), oldFileName);
        if (_notepadService.Open(Path.Combine(containingDirectory, newFileName))) {
        }
    }

    private void ShowRenameNotice(NoteItem? renamedNote, string oldFileName) {
        RenameNoticeText.Text =
            $"For any unsaved changes, look for the unclosed tab with the old file name: {oldFileName}.";
        _renameBannerTimer.Stop();

        Dispatcher.InvokeAsync(() => {
            FileList.UpdateLayout();

            var targetElement = renamedNote is not null
                ? FileList.ItemContainerGenerator.ContainerFromItem(renamedNote) as FrameworkElement
                : null;

            PositionRenameNotice(targetElement ?? NotesPanel);
            RenameNoticePopup.IsOpen = true;
            _renameBannerTimer.Start();
        }, DispatcherPriority.Loaded);
    }

    private void RenameBannerTimer_Tick(object? sender, EventArgs e) {
        _renameBannerTimer.Stop();
        RenameNoticePopup.IsOpen = false;
    }

    private void PositionRenameNotice(FrameworkElement targetElement) {
        RenameNoticePopup.PlacementTarget = targetElement;

        if (ReferenceEquals(targetElement, NotesPanel)) {
            RenameNoticePopup.HorizontalOffset = Math.Max(16, (NotesPanel.ActualWidth - 220) / 2);
            RenameNoticePopup.VerticalOffset = 68;
            return;
        }

        var targetWidth = Math.Max(targetElement.ActualWidth, 220);
        var targetHeight = Math.Max(targetElement.ActualHeight, 36);
        RenameNoticePopup.HorizontalOffset = Math.Max(8, targetWidth - 228);
        RenameNoticePopup.VerticalOffset = Math.Max(0, (targetHeight - 52) / 2);
    }

    private bool EnsureCurrentNoteCanClose(string actionDescription) {
        if (_notepadService.TryCloseCurrentNote())
            return true;

        MessageBox.Show(
            $"Please save, close, or finish with the open note before trying to {actionDescription}.",
            "Note Still Open",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
        if (parent == null) return null;

        int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++) {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;

            var foundChild = FindVisualChild<T>(child);
            if (foundChild != null) return foundChild;
        }
        return null;
    }

    private IntPtr TemporarilyAllowWindowActivation() {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_isNoActivateTemporarilyStripped)
            return hwnd;

        WindowInterop.StripNoActivate(hwnd);
        _isNoActivateTemporarilyStripped = true;
        return hwnd;
    }

    private void RestoreNoActivateAfterInteraction() {
        if (!_isNoActivateTemporarilyStripped)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.RestoreNoActivate(hwnd);
        _isNoActivateTemporarilyStripped = false;
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) {
        var hwnd = TemporarilyAllowWindowActivation();
        try {
            if (!WindowInterop.SetForegroundWindow(hwnd))
                RestoreNoActivateAfterInteraction();
        } catch {
            RestoreNoActivateAfterInteraction();
            throw;
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e) {
        RestoreNoActivateAfterInteraction();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        _viewModel.FilterText = SearchBox.Text;
    }

    private void OnViewModelFilterTextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(MainWindowViewModel.FilterText) && SearchBox.Text != _viewModel.FilterText)
            SearchBox.Text = _viewModel.FilterText;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(_viewModel.FilterText)) {
            e.Handled = true;
            try {
                _viewModel.ClearFilter();
            } finally {
                RestoreNoActivateAfterInteraction();
            }
            return;
        }

        if (e.Key != Key.Enter
            || FileList.Items.Count == 0
            || FileList.Items[0] is not NoteItem firstDisplayedItem)
            return;

        e.Handled = true;
        FileList.SelectedItem = firstDisplayedItem;

        try {
            if (firstDisplayedItem.IsFolder) {
                if (_viewModel.IsExpandMode)
                    _viewModel.ToggleFolderExpansion(firstDisplayedItem);
                else
                    _viewModel.NavigateTo(firstDisplayedItem.FileName);

                Dispatcher.InvokeAsync(() => FileList.Focus(), DispatcherPriority.Input);
            } else {
                OpenSelectedNote();
            }
        } finally {
            RestoreNoActivateAfterInteraction();
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e) {
        _viewModel.ClearFilter(); // triggers OnViewModelFilterTextChanged → SearchBox.Text = ""
        SearchBox.Focus();
    }

    #endregion

    #region Overlay Button Drag
    private void OverlayButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        BeginDrag(OverlayButton, e);
    }

    private void OverlayButton_MouseMove(object sender, MouseEventArgs e) {
        UpdateDrag(OverlayButton, e);
    }

    private void OverlayButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        var wasDragging = _isDragging;
        EndDrag(OverlayButton, e);

        if (!wasDragging && e.ChangedButton == MouseButton.Left) {
            OverlayButton_Click(OverlayButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void NotesPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        // If the header textbox is in edit mode and has focus, transfer focus to trigger LostFocus event
        // This must happen BEFORE the drag starts and captures the mouse
        if (HeaderTextEdit.IsVisible && HeaderTextEdit.IsFocused) {
            MainCanvas.Focus();
            return;
        }

        if (!CanStartNotesPanelDrag(e.OriginalSource as DependencyObject))
            return;

        BeginDrag(NotesPanel, e);
    }

    private void NotesPanel_PreviewMouseMove(object sender, MouseEventArgs e) {
        UpdateDrag(NotesPanel, e);
    }

    private void NotesPanel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        EndDrag(NotesPanel, e);
    }

    private void BeginDrag(FrameworkElement element, MouseButtonEventArgs e) {
        if (e.ChangedButton != MouseButton.Left)
            return;

        _dragElement = element;
        _isDragging = false;
        _dragStart = e.GetPosition(MainCanvas);
        _dragInitialLeft = GetCanvasLeft(element);
        _dragInitialTop = GetCanvasTop(element);
    }

    private void UpdateDrag(FrameworkElement element, MouseEventArgs e) {
        if (_dragElement != element || !_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = e.GetPosition(MainCanvas) - _dragStart.Value;
        if (delta.Length <= _dragThreshold)
            return;

        if (!_isDragging) {
            _isDragging = true;
            element.CaptureMouse();
        }

        Canvas.SetLeft(element, _dragInitialLeft + delta.X);
        Canvas.SetTop(element, _dragInitialTop + delta.Y);
        Canvas.SetRight(element, double.NaN);
        Canvas.SetBottom(element, double.NaN);
        e.Handled = true;
    }

    private void EndDrag(FrameworkElement element, MouseButtonEventArgs e) {
        if (_dragElement != element)
            return;

        _dragElement = null;
        _dragStart = null;
        if (element.IsMouseCaptured)
            element.ReleaseMouseCapture();

        if (_isDragging) {
            _isDragging = false;
            e.Handled = true;
        }
    }

    private static bool CanStartNotesPanelDrag(DependencyObject? source) {
        return !IsWithinInteractiveControl(source);
    }

    private static bool IsWithinNoteRowActionControl(DependencyObject? source) {
        var current = source;
        while (current is not null) {
            if (current is ButtonBase or TextBox or ScrollBar or Thumb)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static bool IsWithinInteractiveControl(DependencyObject? source) {
        var current = source;
        while (current is not null) {
            if (current is ButtonBase or TextBox or ListBox or ListBoxItem or ScrollBar or Thumb)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child) {
        return child switch {
            Visual or Visual3D => VisualTreeHelper.GetParent(child),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => null
        };
    }

    private double GetCanvasLeft(FrameworkElement element) {
        var left = Canvas.GetLeft(element);
        if (!double.IsNaN(left))
            return left;

        var right = Canvas.GetRight(element);
        return right > 0 ? MainCanvas.ActualWidth - right - element.ActualWidth : 0;
    }

    private double GetCanvasTop(FrameworkElement element) {
        var top = Canvas.GetTop(element);
        if (!double.IsNaN(top))
            return top;

        var bottom = Canvas.GetBottom(element);
        return bottom > 0 ? MainCanvas.ActualHeight - bottom - element.ActualHeight : 0;
    }

    private void NotesPanelResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) {
        var currentLeft = GetCanvasLeft(NotesPanel);
        var currentTop = GetCanvasTop(NotesPanel);
        var maxWidth = Math.Max(NotesPanel.MinWidth, MainCanvas.ActualWidth - currentLeft - 8);
        var maxHeight = Math.Max(NotesPanel.MinHeight, MainCanvas.ActualHeight - currentTop - 8);

        NotesPanel.Width = Math.Clamp(NotesPanel.Width + e.HorizontalChange, NotesPanel.MinWidth, maxWidth);
        NotesPanel.Height = Math.Clamp(NotesPanel.Height + e.VerticalChange, NotesPanel.MinHeight, maxHeight);
        e.Handled = true;
    }
    #endregion

    #region System Tray Icon
    private void TrayIcon_TrayRightMouseUp(object sender, RoutedEventArgs e) {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Temporarily remove WS_EX_NOACTIVATE so SetForegroundWindow works, then restore on close
        int exStyle = WindowInterop.GetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE);
        WindowInterop.SetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE, exStyle & ~WindowInterop.WS_EX_NOACTIVATE);
        WindowInterop.SetForegroundWindow(hwnd);
        var cm = (ContextMenu)Resources["TrayContextMenu"];
        cm.Placement = PlacementMode.MousePoint;
        cm.IsOpen = true;
        void OnMenuClosed(object? cs, RoutedEventArgs ce) {
            cm.Closed -= OnMenuClosed;
            WindowInterop.SetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE, exStyle);
        }
        cm.Closed += OnMenuClosed;
    }


    private void TrayShowHideNotes_Click(object sender, RoutedEventArgs e)
    {
        WindowManager.ToggleWorkspaceVisibility();
    }


    private void TraySettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsButton_Click(sender, e);
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    #endregion

    internal void CleanupResources() {
        if (_cleanupCompleted)
            return;

        _cleanupCompleted = true;

        // cleanup global hotkey
        _globalHotkeysService?.Dispose();
        _globalHotkeysService = null;
        _mainHotkeyRegistration = null;
        _additionalMainHotkeyRegistration = null;
        _checklistHotkeyRegistration = null;
        _dictionaryHotkeyRegistration = null;
        _hotkeysInitialized = false;

        // cleanup tray icon
        if (_trayIcon is not null) {
            _trayIcon.TrayRightMouseUp -= TrayIcon_TrayRightMouseUp;
            _trayIcon?.Dispose();
            _trayIcon = null;
        }

        // timers, popups, event handlers
        _renameBannerTimer.Tick -= RenameBannerTimer_Tick;
        _renameBannerTimer.Stop();
        RenameNoticePopup.IsOpen = false;
        
        OverlayButton.PreviewMouseLeftButtonDown -= OverlayButton_MouseLeftButtonDown;
        OverlayButton.PreviewMouseMove -= OverlayButton_MouseMove;
        OverlayButton.PreviewMouseLeftButtonUp -= OverlayButton_MouseLeftButtonUp;
        
        NotesPanel.PreviewMouseLeftButtonDown -= NotesPanel_PreviewMouseLeftButtonDown;
        NotesPanel.PreviewMouseMove -= NotesPanel_PreviewMouseMove;
        NotesPanel.PreviewMouseLeftButtonUp -= NotesPanel_PreviewMouseLeftButtonUp;
        NotesPanel.MouseEnter -= NotesPanel_MouseEnter;
        NotesPanel.MouseLeave -= NotesPanel_MouseLeave;

        GhostModeOpacitySlider.ValueChanged -= GhostModeOpacitySlider_ValueChanged;
        DefaultOpacitySlider.ValueChanged -= DefaultOpacitySlider_ValueChanged;

        MainCanvas.MouseLeftButtonDown -= MainCanvas_MouseLeftButtonDown;
        PreviewMouseDown -= MainWindow_PreviewMouseDown;
        KeyDown -= MainWindow_KeyDown;
        
        HeaderText.MouseLeftButtonDown -= HeaderText_MouseLeftButtonDown;
        HeaderTextEdit.LostFocus -= HeaderEditBox_LostFocus;
        HeaderTextEdit.KeyDown -= HeaderEditBox_KeyDown;
        SearchBox.TextChanged -= SearchBox_TextChanged;
        SearchBox.GotFocus -= SearchBox_GotFocus;
        SearchBox.LostFocus -= SearchBox_LostFocus;
        _viewModel.PropertyChanged -= OnViewModelFilterTextChanged;

        _viewModel.NotesLoaded -= OnNotesLoaded;
        _viewModel.Dispose();
        _notepadService.Dispose();
        _fileService.Dispose();
    }
}
