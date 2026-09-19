using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace Noted;



public partial class MainWindow : Window {
    // services
    private readonly IAppSettingsService _settingsService;
    private readonly INoteFileService _fileService;
    private readonly NoteEditorWindow _noteEditor;
    private readonly Func<FullBackupResult> _createOrUpdateUserBackup;
    private readonly Func<FullBackupInfo> _getUserBackupInfo;
    private readonly Func<FullBackupPreview> _getUserBackupPreview;
    private readonly Func<Guid, BackupFileSummary, BackupFileContent> _readUserBackupFile;
    private readonly Func<string, FullBackupPreview> _getBackupImportPreview;
    private readonly Func<string, Guid, string, BackupFileSummary, BackupFileContent> _readBackupImportFile;
    private readonly Func<string, BackupExportResult> _exportUserBackup;
    private readonly Action _requestUserBackupRestore;
    private readonly Action<string, Guid, string> _requestBackupImportRestore;
    private readonly MainWindowViewModel _viewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly CanvasEdgeResizer _notesPanelResizer;
    private readonly HotkeyConfiguration _hotkeys;
    private TrayIcon? _trayIcon;

    // UI state
    private bool _cleanupCompleted;
    private bool _isNoActivateTemporarilyStripped;
    private bool _isCommittingRename;


    // drag
    private Point? _dragStart;
    private FrameworkElement? _dragElement;
    private bool _isDragging;
    private double _dragInitialLeft;
    private double _dragInitialTop;
    private const double DragThreshold = 5.0;

    internal MainWindow(
            IAppSettingsService settingsService,
            INoteFileService fileService,
            NoteEditorWindow noteEditor,
            IRunOnStartupService runOnStartupService,
            Func<FullBackupResult> createOrUpdateUserBackup,
            Func<FullBackupInfo> getUserBackupInfo,
            Func<FullBackupPreview> getUserBackupPreview,
            Func<Guid, BackupFileSummary, BackupFileContent> readUserBackupFile,
            Func<string, FullBackupPreview> getBackupImportPreview,
            Func<string, Guid, string, BackupFileSummary, BackupFileContent> readBackupImportFile,
            Func<string, BackupExportResult> exportUserBackup,
            Action requestUserBackupRestore,
            Action<string, Guid, string> requestBackupImportRestore,
            Action<AppThemeMode, string> applyAppTheme
        )
    {
        InitializeComponent();
        _notesPanelResizer = new CanvasEdgeResizer(NotesPanel, MainCanvas, margin: 8);

        _settingsService = settingsService;
        _hotkeys = new HotkeyConfiguration(settingsService, () => new GlobalHotkeysService(this), GetHotkeyAction);
        _fileService = fileService;
        _noteEditor = noteEditor;
        _createOrUpdateUserBackup = createOrUpdateUserBackup;
        _getUserBackupInfo = getUserBackupInfo;
        _getUserBackupPreview = getUserBackupPreview;
        _readUserBackupFile = readUserBackupFile;
        _getBackupImportPreview = getBackupImportPreview;
        _readBackupImportFile = readBackupImportFile;
        _exportUserBackup = exportUserBackup;
        _requestUserBackupRestore = requestUserBackupRestore;
        _requestBackupImportRestore = requestBackupImportRestore;

        _viewModel = new MainWindowViewModel(_fileService, _settingsService, action => Dispatcher.Invoke(action));

        _settingsViewModel = new SettingsViewModel(settingsService, fileService, runOnStartupService,
            _hotkeys, applyAppTheme, HandleSettingsAction);
        SettingsView.DataContext = _settingsViewModel;
        DataContext = _viewModel;
        _settingsViewModel.PreferenceChanged += OnSettingsPreferenceChanged;
        _settingsViewModel.NoticeRaised += ShowSettingsNotice;

        InitializeEventHandlers();
        InitializeNotesView();
        RefreshUserBackupControls();
    }

    private void InitializeEventHandlers()
    {
        // overlay button drag
        FloatingNotesButton.PreviewMouseLeftButtonDown += FloatingNotesButton_MouseLeftButtonDown;
        FloatingNotesButton.PreviewMouseMove += FloatingNotesButton_MouseMove;
        FloatingNotesButton.PreviewMouseLeftButtonUp += FloatingNotesButton_MouseLeftButtonUp;
        // notes panel drag
        NotesPanel.PreviewMouseLeftButtonDown += NotesPanel_PreviewMouseLeftButtonDown;
        NotesPanel.PreviewMouseMove += NotesPanel_PreviewMouseMove;
        NotesPanel.PreviewMouseLeftButtonUp += NotesPanel_PreviewMouseLeftButtonUp;
        MainCanvas.MouseLeftButtonDown += MainCanvas_MouseLeftButtonDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        KeyDown += MainWindow_KeyDown;
        SourceInitialized += MainWindow_SourceInitialized;
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.GotFocus += SearchBox_GotFocus;
        SearchBox.LostFocus += SearchBox_LostFocus;
        _noteEditor.NewNoteRequested += NoteEditor_NewNoteRequested;
        _noteEditor.DeleteNoteRequested += NoteEditor_DeleteNoteRequested;
        _noteEditor.NoteRenameRequested += NoteEditor_NoteRenameRequested;
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

    private void HeaderEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        EndHeaderEdit(save: true);
    }

    private void HeaderEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndHeaderEdit(save: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndHeaderEdit(save: false);
            e.Handled = true;
        }
    }

    private void HeaderText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _viewModel.IsSettingsVisible)
            return;

        e.Handled = true;
        Dispatcher.BeginInvoke(BeginHeaderEdit, DispatcherPriority.Input);
    }

    private void BeginHeaderEdit()
    {
        var handle = new WindowInteropHelper(this).Handle;
        WindowInterop.StripNoActivate(handle);
        Activate();
        WindowInterop.SetForegroundWindow(handle);

        _viewModel.HeaderTextEdit = _viewModel.EditableHeaderText;
        _viewModel.IsHeaderEditing = true;
        HeaderTextEdit.Focus();
        Keyboard.Focus(HeaderTextEdit);
        HeaderTextEdit.SelectAll();
    }

    private void EndHeaderEdit(bool save)
    {
        if (!_viewModel.IsHeaderEditing)
            return;

        _viewModel.IsHeaderEditing = false;
        WindowInterop.RestoreNoActivate(new WindowInteropHelper(this).Handle);

        if (!save)
            return;

        var newHeader = HeaderTextEdit.Text.Trim();
        _settingsService.SaveCustomHeader(newHeader);
        _viewModel.SetCustomHeader(newHeader);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e) {
        // overlay: never let Windows treat as foreground fullscreen (prevents DND)
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = WindowInterop.GetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE);
        WindowInterop.SetWindowLong(hwnd, WindowInterop.GWL_EXSTYLE,
            exStyle | WindowInterop.WS_EX_TOOLWINDOW | WindowInterop.WS_EX_NOACTIVATE);
        WindowInterop.SetWindowPos(hwnd, WindowInterop.HWND_TOPMOST, 0, 0, 0, 0,
            WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE);
        // NonRudeHWND: opt out of rude app detection
        WindowInterop.SetProp(hwnd, "NonRudeHWND", new IntPtr(1));
        _trayIcon = TrayIcon.TryCreate(hwnd, "Noted.", TrayIcon_RightClicked);
    }


    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        // Initialize Quick Note button visibility based on mode
        var newNoteMode = _settingsService.LoadNewNoteMode();
        QuickNoteButton.Visibility = newNoteMode == NewNoteMode.Both ? Visibility.Visible : Visibility.Collapsed;

        // overlay: span all monitors
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Dispatcher.BeginInvoke(
            PositionNotedOnPreferredDisplay,
            DispatcherPriority.Loaded);

        InitializeGlobalHotkeys();

        WindowAppearance.Refresh(this);
    }

    private void InitializeGlobalHotkeys()
    {
        var warnings = _hotkeys.Initialize();
        _settingsViewModel.SyncHotkeys(restoreEditorToActive: true);
        if (warnings.Count > 0)
            AppDialog.Show(string.Join("\n\n", warnings), "Hotkey Registration Notice",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnNotesHotkeyPressed()
    {
        WindowManager.ToggleNotedWindows();
    }
    private void OnChecklistHotkeyPressed()
    {
        WindowManager.ToggleChecklist();
    }
    private void OnDictionaryHotkeyPressed()
    {
        WindowManager.ToggleDictionary();
    }

    private void OnNotesLoaded(object? sender, EventArgs e) {
        UpdateNotesDirectoryDisplay();
        if (_viewModel.SelectedNoteKey is not null)
            FileList.SelectedItem = _viewModel.FindNote(_viewModel.SelectedNoteKey);
    }



    private void UpdateNotesDirectoryDisplay() {
        _settingsViewModel.RefreshNotesDirectory();
        SettingsButton.ToolTip = SettingsView.Visibility == Visibility.Visible
            ? "Return to notes"
            : "Open settings";
    }


    private void PinNoteButton_Click(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement element && element.DataContext is NoteItem note) {
            _viewModel.TogglePin(note);
        }
    }

    private void SetSettingsViewVisible(bool setVisible) {
        NotesListView.Visibility = setVisible ? Visibility.Collapsed : Visibility.Visible;
        SettingsView.Visibility = setVisible ? Visibility.Visible : Visibility.Collapsed;
        UtilityMenu.Visibility = setVisible ? Visibility.Collapsed : Visibility.Visible;
        NewNoteButton.Visibility = setVisible ? Visibility.Collapsed : Visibility.Visible;
        QuickNoteButton.Visibility = setVisible
            ? Visibility.Collapsed
            : _settingsService.LoadNewNoteMode() == NewNoteMode.Both
                ? Visibility.Visible
                : Visibility.Collapsed;
        SettingsButtonIcon.Text = setVisible ? "\uE72B" : "\uE713";
        AutomationProperties.SetName(SettingsButton, setVisible ? "Return to notes" : "Settings");
        _viewModel.IsSettingsVisible = setVisible;
        UpdateNotesDirectoryDisplay();
        // Restore correct opacity when closing settings (slider may have previewed a value)
        if (!setVisible)
            WindowAppearance.Refresh(this);
    }

    private void UpdateSettingsView()
    {
        _settingsViewModel.Reload();
        _viewModel.ShowModifiedSubtitle = _settingsViewModel.ShowModifiedSubtitle;
        _viewModel.FolderNavigationMode = _settingsViewModel.FolderNavigationMode;
        QuickNoteButton.Visibility = _settingsViewModel.NewNoteMode == NewNoteMode.Both
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSettingsPreferenceChanged(string property)
    {
        switch (property)
        {
            case nameof(SettingsViewModel.ShowModifiedSubtitle):
                _viewModel.ShowModifiedSubtitle = _settingsViewModel.ShowModifiedSubtitle;
                break;
            case nameof(SettingsViewModel.FolderNavigationMode):
                _viewModel.FolderNavigationMode = _settingsViewModel.FolderNavigationMode;
                break;
            case nameof(SettingsViewModel.ShowNotesDirectory):
                _viewModel.ShowNotesDirectory = _settingsViewModel.ShowNotesDirectory;
                break;
            case nameof(SettingsViewModel.NewNoteMode):
                if (SettingsView.Visibility != Visibility.Visible)
                    QuickNoteButton.Visibility = _settingsViewModel.NewNoteMode == NewNoteMode.Both ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(SettingsViewModel.PreferredDisplay):
                Dispatcher.BeginInvoke(PositionNotedOnPreferredDisplay, DispatcherPriority.Loaded);
                break;
            case nameof(SettingsViewModel.GhostModeEnabled):
                if (!_settingsViewModel.GhostModeEnabled || IsNotesPanelVisible && !NotesPanel.IsMouseOver)
                    WindowAppearance.Refresh(this);
                break;
            case nameof(SettingsViewModel.GhostOpacityPercent):
                if (IsNotesPanelVisible) WindowAppearance.Refresh(this);
                break;
            case nameof(SettingsViewModel.DefaultOpacityPercent):
                if (IsNotesPanelVisible && (!_settingsViewModel.GhostModeEnabled || NotesPanel.IsMouseOver))
                    WindowAppearance.Refresh(this);
                break;
        }
    }

    private static void ShowSettingsNotice(SettingsNotice notice) =>
        AppDialog.Show(notice.Message, notice.Title, MessageBoxButton.OK,
            notice.IsWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    private void HandleSettingsAction(SettingsAction action)
    {
        var args = new RoutedEventArgs();
        switch (action)
        {
            case SettingsAction.ChangeFolder: ChangeFolderButton_Click(this, args); break;
            case SettingsAction.CreateBackup: CreateOrUpdateBackupButton_Click(this, args); break;
            case SettingsAction.ViewBackup: ViewBackupButton_Click(this, args); break;
            case SettingsAction.ExportBackup: ExportBackupButton_Click(this, args); break;
            case SettingsAction.ImportBackup: ImportBackupButton_Click(this, args); break;
            case SettingsAction.RestoreBackup: RestoreBackupButton_Click(this, args); break;
        }
    }

    private void PositionNotedOnPreferredDisplay()
    {
        var screen = ResolvePreferredDisplay();
        if (screen is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var workLeft = screen.WorkLeft / dpi.DpiScaleX - SystemParameters.VirtualScreenLeft;
        var workTop = screen.WorkTop / dpi.DpiScaleY - SystemParameters.VirtualScreenTop;
        var workWidth = screen.WorkWidth / dpi.DpiScaleX;
        var workHeight = screen.WorkHeight / dpi.DpiScaleY;

        var panelWidth = NotesPanel.ActualWidth > 0 ? NotesPanel.ActualWidth : NotesPanel.Width;
        var panelHeight = NotesPanel.ActualHeight > 0 ? NotesPanel.ActualHeight : NotesPanel.Height;
        var panelLeft = Math.Max(workLeft + 8, workLeft + workWidth - panelWidth - 40);
        var panelTop = Math.Clamp(
            workTop + 60,
            workTop + 8,
            Math.Max(workTop + 8, workTop + workHeight - panelHeight - 8));

        Canvas.SetRight(NotesPanel, double.NaN);
        Canvas.SetBottom(NotesPanel, double.NaN);
        Canvas.SetLeft(NotesPanel, panelLeft);
        Canvas.SetTop(NotesPanel, panelTop);

        var buttonWidth = FloatingNotesButton.ActualWidth > 0 ? FloatingNotesButton.ActualWidth : FloatingNotesButton.Width;
        var buttonHeight = FloatingNotesButton.ActualHeight > 0 ? FloatingNotesButton.ActualHeight : FloatingNotesButton.Height;
        Canvas.SetRight(FloatingNotesButton, double.NaN);
        Canvas.SetBottom(FloatingNotesButton, double.NaN);
        Canvas.SetLeft(FloatingNotesButton, workLeft + workWidth - buttonWidth - 20);
        Canvas.SetTop(FloatingNotesButton, workTop + workHeight - buttonHeight - 8);
    }

    private DisplayInfo? ResolvePreferredDisplay()
    {
        var screens = DisplayService.GetDisplays();
        if (!string.IsNullOrWhiteSpace(_settingsViewModel.PreferredDisplayDeviceName))
        {
            var preferred = screens.FirstOrDefault(screen =>
                string.Equals(
                    screen.DeviceName,
                    _settingsViewModel.PreferredDisplayDeviceName,
                    StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        return screens.FirstOrDefault(screen => screen.IsPrimary)
            ?? screens.FirstOrDefault();
    }

    private Action GetHotkeyAction(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => OnNotesHotkeyPressed,
        HotkeyFeature.Checklist => OnChecklistHotkeyPressed,
        HotkeyFeature.Dictionary => OnDictionaryHotkeyPressed,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null)
    };

    internal bool IsNotesPanelVisible => NotesPanel.Visibility == Visibility.Visible;

    internal void ShowNotesPanel() {
        NotesPanel.Visibility = Visibility.Visible;
        WindowAppearance.Refresh(this);
        // force focus for overlay
        var hwnd = TemporarilyAllowWindowActivation();
        try {
            if (!WindowInterop.SetForegroundWindow(hwnd)) {
                RestoreNoActivateAfterInteraction();
                return;
            }

            // focus list after layout
            Dispatcher.BeginInvoke(() => {
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


    internal void HideNotesPanel() {
        try {
            _viewModel.ClearFilter();
        } finally {
            RestoreNoActivateAfterInteraction();
            NotesPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void HideNotesPanelAndEditor() {
        try {
            HideNotesPanel();
        } finally {
            WindowManager.HideEditor();
        }
    }


    private void FloatingNotesButton_Click(object sender, RoutedEventArgs e) {
        WindowManager.ToggleNotedWindows();
    }


    private void HideNotesButton_Click(object sender, RoutedEventArgs e) {
        if (_settingsViewModel.MainHideButtonHidesAll)
            WindowManager.HideAll();
        else
            HideNotesPanelAndEditor();
    }


    private void SettingsButton_Click(object sender, RoutedEventArgs e) {
        // toggle settings panel
        var showSettings = SettingsView.Visibility != Visibility.Visible;
        if (showSettings)
        {
            UpdateSettingsView();
        }
        else
        {
            // always reload notes after settings
            var showModified = _settingsService.LoadShowModifiedSubtitle();
            _viewModel.ShowModifiedSubtitle = showModified;
            _viewModel.LoadNotes();
        }
        SetSettingsViewVisible(showSettings);
    }


    private void CreateOrUpdateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsViewModel.LastBackupAttemptFailed)
        {
            _settingsViewModel.LastBackupAttemptFailed = false;
            RefreshUserBackupControls();
        }

        var backupInfo = _getUserBackupInfo();
        var action = backupInfo.Exists ? "Update" : "Create";
        var confirmation = AppDialog.Show(
            this,
            $"{action} your full Noted backup from the current data?\n\n" +
            "Noted will build and verify the new backup before replacing the current one. " +
            "The previous backup is kept separately.",
            $"{action} Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        RunBackupCommand(_createOrUpdateUserBackup);
    }

    private void UtilityMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = CompactUtilityMenuButton.ContextMenu;
        if (menu is null)
            return;

        menu.PlacementTarget = CompactUtilityMenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmAndRequestBackupRestore();
    }

    private async void ViewBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var previousCursor = Mouse.OverrideCursor;
        var previousStatus = _settingsViewModel.BackupStatus;
        var previousStatusBrush = _settingsViewModel.BackupStatusResource;
        var createWasEnabled = _settingsViewModel.CanCreateBackup;
        var viewWasEnabled = _settingsViewModel.CanViewBackup;
        var exportWasEnabled = _settingsViewModel.CanExportBackup;
        var importWasEnabled = _settingsViewModel.CanImportBackup;
        var restoreWasEnabled = _settingsViewModel.CanRestoreBackup;
        _settingsViewModel.CanCreateBackup = false;
        _settingsViewModel.CanViewBackup = false;
        _settingsViewModel.CanExportBackup = false;
        _settingsViewModel.CanImportBackup = false;
        _settingsViewModel.CanRestoreBackup = false;
        _settingsViewModel.BackupStatus = "Checking backup contents...";
        _settingsViewModel.BackupStatusResource = "NotedSecondaryTextBrush";
        Mouse.OverrideCursor = Cursors.Wait;

        FullBackupPreview preview;
        try
        {
            preview = await Task.Run(_getUserBackupPreview);
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
            _settingsViewModel.CanCreateBackup = createWasEnabled;
            _settingsViewModel.CanViewBackup = viewWasEnabled;
            _settingsViewModel.CanExportBackup = exportWasEnabled;
            _settingsViewModel.CanImportBackup = importWasEnabled;
            _settingsViewModel.CanRestoreBackup = restoreWasEnabled;
            _settingsViewModel.BackupStatus = previousStatus;
            _settingsViewModel.BackupStatusResource = previousStatusBrush;
        }

        if (!preview.IsValid)
        {
            RefreshUserBackupControls();
            AppDialog.Show(
                this,
                preview.Exists
                    ? $"The backup could not be verified: {preview.Error}"
                    : "No backup exists yet.",
                "View Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new BackupPreviewDialog(preview, _readUserBackupFile)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        PositionBackupPreviewDialog(dialog);

        if (dialog.ShowDialog() == true)
            ConfirmAndRequestBackupRestore();
    }

    private async void ExportBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Noted Backup",
            Filter = "Noted Backup (*.notedbackup)|*.notedbackup",
            DefaultExt = ".notedbackup",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Noted Backup {DateTime.Now:yyyy-MM-dd}.notedbackup"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _settingsViewModel.CanCreateBackup = false;
        _settingsViewModel.CanViewBackup = false;
        _settingsViewModel.CanExportBackup = false;
        _settingsViewModel.CanImportBackup = false;
        _settingsViewModel.CanRestoreBackup = false;
        _settingsViewModel.BackupStatus = "Exporting verified backup...";
        _settingsViewModel.BackupStatusResource = "NotedSecondaryTextBrush";
        var previousCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;

        BackupExportResult result;
        FullBackupInfo? refreshedBackupInfo = null;
        try
        {
            result = await Task.Run(() => _exportUserBackup(dialog.FileName));
            refreshedBackupInfo = await Task.Run(_getUserBackupInfo);
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
            if (refreshedBackupInfo is not null)
            {
                _settingsViewModel.UpdateBackupInfo(refreshedBackupInfo);
            }
            else
            {
                _settingsViewModel.CanCreateBackup = true;
            }
        }

        var message = string.IsNullOrWhiteSpace(result.Warning)
            ? result.Message
            : $"{result.Message}\n\n{result.Warning}";
        AppDialog.Show(
            this,
            message,
            result.Success ? "Backup Exported" : "Backup Export Failed",
            MessageBoxButton.OK,
            result.Success && string.IsNullOrWhiteSpace(result.Warning)
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);
    }

    private async void ImportBackupButton_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Import Noted Backup",
            Filter = "Noted Backup (*.notedbackup)|*.notedbackup",
            DefaultExt = ".notedbackup",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog(this) != true)
            return;

        var previousCursor = Mouse.OverrideCursor;
        var previousStatus = _settingsViewModel.BackupStatus;
        var previousStatusBrush = _settingsViewModel.BackupStatusResource;
        var createWasEnabled = _settingsViewModel.CanCreateBackup;
        var viewWasEnabled = _settingsViewModel.CanViewBackup;
        var exportWasEnabled = _settingsViewModel.CanExportBackup;
        var importWasEnabled = _settingsViewModel.CanImportBackup;
        var restoreWasEnabled = _settingsViewModel.CanRestoreBackup;
        _settingsViewModel.CanCreateBackup = false;
        _settingsViewModel.CanViewBackup = false;
        _settingsViewModel.CanExportBackup = false;
        _settingsViewModel.CanImportBackup = false;
        _settingsViewModel.CanRestoreBackup = false;
        _settingsViewModel.BackupStatus = "Checking imported backup...";
        _settingsViewModel.BackupStatusResource = "NotedSecondaryTextBrush";
        Mouse.OverrideCursor = Cursors.Wait;

        FullBackupPreview preview;
        try
        {
            preview = await Task.Run(() => _getBackupImportPreview(fileDialog.FileName));
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
            _settingsViewModel.CanCreateBackup = createWasEnabled;
            _settingsViewModel.CanViewBackup = viewWasEnabled;
            _settingsViewModel.CanExportBackup = exportWasEnabled;
            _settingsViewModel.CanImportBackup = importWasEnabled;
            _settingsViewModel.CanRestoreBackup = restoreWasEnabled;
            _settingsViewModel.BackupStatus = previousStatus;
            _settingsViewModel.BackupStatusResource = previousStatusBrush;
        }

        if (!preview.IsValid)
        {
            AppDialog.Show(
                this,
                preview.Error ?? "The selected file is not a valid Noted backup.",
                "Import Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var verificationToken = preview.VerificationToken;
        if (string.IsNullOrWhiteSpace(verificationToken))
        {
            AppDialog.Show(
                this,
                "The selected backup could not be tied to this preview. Check it again before restoring.",
                "Import Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var previewDialog = new BackupPreviewDialog(
            preview,
            (backupId, file) => _readBackupImportFile(
                fileDialog.FileName,
                backupId,
                verificationToken,
                file),
            isImport: true)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        PositionBackupPreviewDialog(previewDialog);
        if (previewDialog.ShowDialog() != true)
            return;

        var backupDate = preview.CreatedUtc?.ToLocalTime().ToString("MMM d, yyyy h:mm tt")
            ?? "an unknown date";
        var confirmation = AppDialog.Show(
            this,
            $"Restore all Noted data from the imported backup dated {backupDate}?\n\n" +
            "Noted will save pending work, close, and restore the verified files into this installation. " +
            "Your existing user backup and the selected backup file will not be changed.",
            "Restore Imported Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation == MessageBoxResult.Yes)
            _requestBackupImportRestore(
                fileDialog.FileName,
                preview.BackupId,
                verificationToken);
    }

    private void PositionBackupPreviewDialog(BackupPreviewDialog dialog)
    {
        var panelLocation = NotesPanel.TranslatePoint(new Point(0, 0), this);
        var bounds = WindowInterop.NormalizeWindowBounds(
            Left + panelLocation.X + ((NotesPanel.ActualWidth - dialog.Width) / 2),
            Top + panelLocation.Y + ((NotesPanel.ActualHeight - dialog.Height) / 2),
            dialog.Width,
            dialog.Height,
            dialog.MinWidth,
            dialog.MinHeight,
            dialog.Width,
            dialog.Height);
        dialog.Left = bounds.Left;
        dialog.Top = bounds.Top;
        dialog.Width = bounds.Width;
        dialog.Height = bounds.Height;
    }

    private void ConfirmAndRequestBackupRestore()
    {
        var backupInfo = _getUserBackupInfo();
        if (!backupInfo.IsValid)
        {
            RefreshUserBackupControls();
            return;
        }

        var backupDate = backupInfo.LastUpdatedUtc?.ToLocalTime().ToString("MMM d, yyyy h:mm tt")
            ?? "an unknown date";
        var confirmation = AppDialog.Show(
            this,
            $"Restore all backed-up Noted data from {backupDate}?\n\n" +
            "Noted will save pending work, close, and restore the verified files. " +
            "The backup itself will not be changed.",
            "Restore Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation == MessageBoxResult.Yes)
            _requestUserBackupRestore();
    }

    private void RunBackupCommand(Func<FullBackupResult> command)
    {
        _settingsViewModel.CanCreateBackup = false;
        _settingsViewModel.CanViewBackup = false;
        _settingsViewModel.CanExportBackup = false;
        _settingsViewModel.CanImportBackup = false;
        _settingsViewModel.CanRestoreBackup = false;
        _settingsViewModel.BackupStatus = "Checking and copying current data...";
        _settingsViewModel.BackupStatusResource = "NotedSecondaryTextBrush";
        var previousCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;

        FullBackupResult result;
        try
        {
            result = command();
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
            _settingsViewModel.CanCreateBackup = true;
        }

        var backupInfo = _getUserBackupInfo();
        _settingsViewModel.ShowBackupResult(result, backupInfo);
        RefreshUserBackupControls(updateStatusText: false);
    }

    private void RefreshUserBackupControls(bool updateStatusText = true)
    {
        _settingsViewModel.UpdateBackupInfo(_getUserBackupInfo(), updateStatusText);
    }

    private void ChecklistButton_Click(object sender, RoutedEventArgs e)
    {
        WindowManager.ToggleChecklist();
    }

    private void DictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        WindowManager.ToggleDictionary();
    }

    private void ScratchpadButton_Click(object sender, RoutedEventArgs e)
    {
        WindowManager.ToggleScratchpad();
    }

    private void MiniPadButton_Click(object sender, RoutedEventArgs e)
    {
        WindowManager.ToggleMiniPad();
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
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ExceptionDiagnostics.Record(ex);
            AppDialog.Show("The note name could not be copied. The clipboard may be in use; try again.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void NewNoteButton_Click(object sender, RoutedEventArgs e) {
        CreateAndOpenNewNote(this);
    }

    private void NoteEditor_NewNoteRequested(object? sender, EventArgs e) {
        CreateAndOpenNewNote(_noteEditor);
    }

    private void NoteEditor_NoteRenameRequested(
        object? sender,
        NoteRenameRequestedEventArgs e) {
        var attemptedName = Path.GetFileNameWithoutExtension(e.FilePath);
        var oldFileName = Path.GetFileName(e.FilePath);
        var containingDirectory = Path.GetDirectoryName(e.FilePath) ?? _fileService.CurrentDirectory;
        if (!_fileService.TryGetNoteKey(e.FilePath, out var oldNoteKey))
        {
            e.IsCanceled = true;
            AppDialog.Show(
                this,
                "This file is outside the notes folder and cannot be renamed here. Use Save As to choose a new name or location.",
                "Rename Note",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        while (true) {
            var dialog = e.IsFirstSave
                ? NewNoteNameDialog.ForFirstSave(attemptedName)
                : NewNoteNameDialog.ForRename(attemptedName);
            dialog.Owner = _noteEditor;
            if (dialog.ShowDialog() != true) {
                e.IsCanceled = true;
                return;
            }

            attemptedName = dialog.NoteName;
            var (success, newFileName, error) = _fileService.RenameNote(
                oldFileName,
                attemptedName,
                containingDirectory);
            if (!success) {
                AppDialog.Show(
                    _noteEditor,
                    error ?? "The note could not be renamed.",
                    e.IsFirstSave ? "Name Note" : "Rename Note",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                continue;
            }

            var renamedFilePath = Path.Combine(
                containingDirectory,
                newFileName ?? oldFileName);
            var renamedNoteKey = _fileService.GetNoteKey(renamedFilePath);
            _viewModel.ReplacePinnedNoteKey(oldNoteKey, renamedNoteKey);
            e.NewFilePath = renamedFilePath;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => _viewModel.LoadNotes(renamedNoteKey)));
            return;
        }
    }

    private void CreateAndOpenNewNote(Window owner) {
        try {
            var createdNote = CreateNewNoteFromCurrentSettings(owner);
            if (createdNote is null)
                return;

            var filePath = Path.Combine(_fileService.CurrentDirectory, createdNote.FileName);
            _viewModel.LoadNotes(_fileService.GetNoteKey(filePath));
            // OnNotesLoaded fires synchronously above, so selection is already set.
            OpenCreatedNoteInEditor(filePath, createdNote.UsesGeneratedName, createdNote.InitialContent);
        } catch (Exception ex) when (FileSystemErrors.IsExpected(ex)) {
            ExceptionDiagnostics.Record(ex);
            AppDialog.Show(owner, "The note could not be created. Check folder access and available disk space.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void QuickNoteButton_Click(object sender, RoutedEventArgs e) {
        try {
            var (createdNote, error) = _fileService.CreateNote();
            if (createdNote is null)
            {
                AppDialog.Show(error ?? "The note could not be created.", "New Note", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var filePath = Path.Combine(_fileService.CurrentDirectory, createdNote.FileName);
            _viewModel.LoadNotes(_fileService.GetNoteKey(filePath));
            OpenCreatedNoteInEditor(filePath, createdNote.UsesGeneratedName, createdNote.InitialContent);
        } catch (Exception ex) when (FileSystemErrors.IsExpected(ex)) {
            ExceptionDiagnostics.Record(ex);
            AppDialog.Show("The quick note could not be created. Check folder access and available disk space.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private CreatedNote? CreateNewNoteFromCurrentSettings(Window owner) {
        var mode = _settingsService.LoadNewNoteMode();

        // In "Quick" mode, skip the dialog
        if (mode == NewNoteMode.Quick)
            return _fileService.CreateNote().Note;

        // In "Prompt" or "Both" modes, offer a date name that can be accepted or replaced.
        var attemptedName = _fileService.SuggestNoteName();
        while (true) {
            var dialog = new NewNoteNameDialog(attemptedName) {
                Owner = owner
            };

            if (dialog.ShowDialog() != true)
                return null;

            var (note, error) = _fileService.CreateNote(dialog.NoteName);
            if (note is not null)
                return note;

            attemptedName = dialog.NoteName;
            AppDialog.Show(owner, error ?? "The note name is invalid.",
                "New Note Name", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
            var msg = $"Delete folder '{note.DisplayName}' and all its contents?";
            if (AppDialog.Show(msg, "Delete Folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var folderPath = Path.Combine(_fileService.CurrentDirectory, note.FileName);
            if (!_noteEditor.TryPrepareForDirectoryRemoval(folderPath))
                return;

            var (success, error) = _fileService.DeleteFolder(note.FileName);
            if (success)
                _noteEditor.NotifyDirectoryRemoved(folderPath);
            else
                AppDialog.Show(error ?? "Failed to delete folder.", "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            _viewModel.LoadNotes();
        } else {
            var containingDirectory = note.FullPath is null
                ? _fileService.CurrentDirectory
                : Path.GetDirectoryName(note.FullPath);
            var filePath = note.FullPath ?? Path.Combine(containingDirectory!, note.FileName);
            DeleteNote(filePath, note.DisplayName, this);
        }
    }

    private void NoteEditor_DeleteNoteRequested(
        object? sender,
        NoteDeleteRequestedEventArgs e) {
        DeleteNote(
            e.FilePath,
            NoteNameFormatter.Format(
                Path.GetFileName(e.FilePath),
                keepExtensionForCustomName: false),
            _noteEditor);
    }

    private void DeleteNote(string filePath, string displayName, Window owner) {
        if (_settingsService.LoadConfirmNoteDeletion()) {
            var confirmation = AppDialog.ShowWithSuppression(
                owner,
                $"Delete '{displayName}'?\n\nThe note will be kept in Deleted Notes for 14 days.",
                "Delete Note",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                "Don't show this warning again");
            if (confirmation.Result != MessageBoxResult.Yes)
                return;
            if (confirmation.DoNotShowAgain) {
                _settingsViewModel.ConfirmNoteDeletion = false;
            }
        }

        if (!_noteEditor.TryPrepareForFileRemoval(filePath))
            return;

        try {
            var containingDirectory = Path.GetDirectoryName(filePath);
            if (_fileService.DeleteNote(Path.GetFileName(filePath), containingDirectory)) {
                _noteEditor.NotifyFileRemoved(filePath);
                _viewModel.LoadNotes();
                return;
            }

            AppDialog.Show(
                owner,
                "The note could not be deleted.",
                "Delete Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        } catch (Exception ex) when (FileSystemErrors.IsExpected(ex)) {
            ExceptionDiagnostics.Record(ex);
            AppDialog.Show(
                owner,
                "The note could not be deleted. Check folder access and whether the file is in use.",
                "Delete Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (FileList.SelectedItem is NoteItem note)
            _viewModel.SelectedNoteKey = note.IsFolder ? null : note.NoteKey;
    }

    private void OpenSelectedNote() {
        if (FileList.SelectedItem is not NoteItem note) return;

        // FullPath is set for child notes in Expand mode; drill-down mode uses the current folder.
        var filePath = note.FullPath ?? Path.Combine(_fileService.CurrentDirectory, note.FileName);
        _viewModel.SelectedNoteKey = note.NoteKey;

        OpenNoteInEditor(filePath);
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
            AppDialog.Show(error ?? "Failed to create folder.", "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var selectedDirectory = Path.GetFullPath(dialog.FolderName);
            if (string.Equals(selectedDirectory, _fileService.NotesDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            if (!_noteEditor.TryPrepareToCloseAllDocuments())
                return;

            var changed = _fileService.ChangeNotesDirectory(selectedDirectory);
            _viewModel.SelectedNoteKey = null;
            if (changed) {
                _noteEditor.CloseAllDocuments();
                _noteEditor.HideWindow();
                _viewModel.ClearFilter();
                _viewModel.LoadNotes();
            }

            UpdateNotesDirectoryDisplay();
            UpdateSettingsView();
        } catch (Exception ex) when (ex is SettingsPersistenceException || FileSystemErrors.IsExpected(ex)) {
            ExceptionDiagnostics.Record(ex);
            AppDialog.Show(ex is SettingsPersistenceException
                    ? ex.Message
                    : "The notes folder could not be changed. Check folder access and try again.",
                "Folder Change Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (NotesPanel.Visibility == Visibility.Visible &&
            !NotesPanel.IsMouseOver && !FloatingNotesButton.IsMouseOver) {
            HideNotesPanelAndEditor();
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape && NotesPanel.Visibility == Visibility.Visible) {
            HideNotesPanelAndEditor();
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
        var hwnd = TemporarilyAllowWindowActivation();
        try {
            if (!WindowInterop.SetForegroundWindow(hwnd)) {
                RestoreNoActivateAfterInteraction();
                return;
            }

            Activate();
            note.EditableName = Path.GetFileNameWithoutExtension(note.FileName);
            note.IsEditing = true;

            Dispatcher.BeginInvoke(() => {
                try {
                    var listBoxItem = FileList.ItemContainerGenerator.ContainerFromItem(note) as ListBoxItem;
                    var textBox = listBoxItem is null
                        ? null
                        : VisualTreeHelpers.FindDescendant<TextBox>(listBoxItem);
                    if (textBox is null) {
                        RestoreNoActivateAfterInteraction();
                        return;
                    }

                    textBox.Focus();
                    Keyboard.Focus(textBox);
                    textBox.SelectAll();
                } catch {
                    RestoreNoActivateAfterInteraction();
                    throw;
                }
            }, DispatcherPriority.Input);
        } catch {
            RestoreNoActivateAfterInteraction();
            throw;
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key is not (Key.Return or Key.Escape))
            return;

        e.Handled = true;
        if (sender is not TextBox textBox)
            return;

        if (e.Key == Key.Escape) {
            CancelRename(textBox);
            return;
        }

        CommitRename(textBox);
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e) {
        if (!_isCommittingRename && sender is TextBox textBox)
            CancelRename(textBox);
    }

    private void CancelRename(TextBox textBox) {
        try {
            if (textBox.DataContext is not NoteItem note || !note.IsEditing)
                return;

            note.IsEditing = false;
            note.EditableName = Path.GetFileNameWithoutExtension(note.FileName);
        } finally {
            RestoreNoActivateAfterInteraction();
        }
    }

    private void CommitRename(TextBox textBox) {
        if (textBox.DataContext is not NoteItem note || !note.IsEditing)
            return;

        var originalDisplayName = Path.GetFileNameWithoutExtension(note.FileName);
        var requestedEditedName = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(requestedEditedName)
            || string.Equals(requestedEditedName, originalDisplayName, StringComparison.Ordinal)) {
            CancelRename(textBox);
            return;
        }

        _isCommittingRename = true;
        var renameSucceeded = false;
        try {
            renameSucceeded = note.IsFolder
                ? CommitFolderRename(note, requestedEditedName)
                : CommitNoteRename(note, requestedEditedName);

            if (renameSucceeded) {
                note.IsEditing = false;
                note.EditableName = originalDisplayName;
                RestoreNoActivateAfterInteraction();
                return;
            }

            note.EditableName = requestedEditedName;
            note.IsEditing = true;
        } finally {
            _isCommittingRename = false;
        }

        _ = Dispatcher.BeginInvoke(() => {
            try {
                var listBoxItem = FileList.ItemContainerGenerator.ContainerFromItem(note) as ListBoxItem;
                var renameTextBox = listBoxItem is null
                    ? null
                    : VisualTreeHelpers.FindDescendant<TextBox>(listBoxItem);
                if (renameTextBox is null) {
                    RestoreNoActivateAfterInteraction();
                    return;
                }

                renameTextBox.Focus();
                Keyboard.Focus(renameTextBox);
                renameTextBox.SelectAll();
            } catch {
                RestoreNoActivateAfterInteraction();
                throw;
            }
        }, DispatcherPriority.Input);
    }

    private bool CommitFolderRename(NoteItem folder, string newName) {
        var oldFolderPath = Path.Combine(_fileService.CurrentDirectory, folder.FileName);
        var (success, newFolderName, error) = _fileService.RenameFolder(folder.FileName, newName);
        if (!success) {
            AppDialog.Show(error ?? "Rename failed.", "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (newFolderName is not null) {
            var newFolderPath = Path.Combine(_fileService.CurrentDirectory, newFolderName);
            _noteEditor.NotifyDirectoryRenamed(oldFolderPath, newFolderPath);
        }

        _viewModel.LoadNotes();
        return true;
    }

    private bool CommitNoteRename(NoteItem note, string newDisplayName) {
        var oldFileName = note.FileName;
        var containingDirectory = note.FullPath is null
            ? _fileService.CurrentDirectory
            : Path.GetDirectoryName(note.FullPath) ?? _fileService.CurrentDirectory;
        var oldFilePath = Path.Combine(containingDirectory, oldFileName);

        var (success, newFileName, error) = _fileService.RenameNote(
            note.FileName,
            newDisplayName,
            containingDirectory);
        if (!success) {
            if (error is not null)
                AppDialog.Show(error, "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var renamedFileName = newFileName ?? note.FileName;
        var renamedFilePath = Path.Combine(containingDirectory, renamedFileName);
        _noteEditor.NotifyFileRenamed(oldFilePath, renamedFilePath);
        var renamedNoteKey = _fileService.GetNoteKey(renamedFilePath);
        _viewModel.ReplacePinnedNoteKey(note.NoteKey, renamedNoteKey);
        _viewModel.LoadNotes(renamedNoteKey);
        return true;
    }

    private bool OpenNoteInEditor(string filePath) {
        if (_noteEditor.OpenNote(filePath))
            return true;

        RestoreSelectionToOpenEditorNote();
        return false;
    }

    private bool OpenCreatedNoteInEditor(string filePath, bool usesGeneratedName, string initialContent) {
        if (!_noteEditor.OpenCreatedNote(filePath, usesGeneratedName, initialContent)) {
            RestoreSelectionToOpenEditorNote();
            return false;
        }

        _noteEditor.BeginEditingCreatedNote(
            moveCaretToEnd: _settingsService.LoadTimestampPlacement() == NoteTimestampPlacement.Top);

        return true;
    }

    private void RestoreSelectionToOpenEditorNote() {
        var openFilePath = _noteEditor.OpenFilePath;
        var openNoteKey = openFilePath is null ? null : _fileService.GetNoteKey(openFilePath);
        _viewModel.SelectedNoteKey = openNoteKey;
        FileList.SelectedItem = openNoteKey is null ? null : _viewModel.FindNote(openNoteKey);
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

                Dispatcher.BeginInvoke(() => FileList.Focus(), DispatcherPriority.Input);
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
    private void FloatingNotesButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        BeginDrag(FloatingNotesButton, e);
    }

    private void FloatingNotesButton_MouseMove(object sender, MouseEventArgs e) {
        UpdateDrag(FloatingNotesButton, e);
    }

    private void FloatingNotesButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        var wasDragging = _isDragging;
        EndDrag(FloatingNotesButton, e);

        if (!wasDragging && e.ChangedButton == MouseButton.Left) {
            FloatingNotesButton_Click(FloatingNotesButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void NotesPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (_notesPanelResizer.IsResizing || _notesPanelResizer.IsNearEdge(e.GetPosition(NotesPanel)))
            return;

        if (HeaderTextEdit.IsVisible && HeaderTextEdit.IsFocused) {
            MainCanvas.Focus();
            return;
        }

        if (!CanStartNotesPanelDrag(e.OriginalSource as DependencyObject))
            return;

        BeginDrag(NotesPanel, e);
    }

    private void NotesPanel_PreviewMouseMove(object sender, MouseEventArgs e) {
        if (_notesPanelResizer.IsResizing)
            return;

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
        if (delta.Length <= DragThreshold)
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
            Visual => VisualTreeHelper.GetParent(child),
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

    #endregion

    #region System Tray Icon
    private void TrayIcon_RightClicked() {
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
        WindowManager.ToggleNotedWindows();
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

        _settingsViewModel.PreferenceChanged -= OnSettingsPreferenceChanged;
        _settingsViewModel.NoticeRaised -= ShowSettingsNotice;
        _hotkeys.Dispose();
        _notesPanelResizer.Dispose();

        // cleanup tray icon
        _trayIcon?.Dispose();
        _trayIcon = null;

        // event handlers
        FloatingNotesButton.PreviewMouseLeftButtonDown -= FloatingNotesButton_MouseLeftButtonDown;
        FloatingNotesButton.PreviewMouseMove -= FloatingNotesButton_MouseMove;
        FloatingNotesButton.PreviewMouseLeftButtonUp -= FloatingNotesButton_MouseLeftButtonUp;

        NotesPanel.PreviewMouseLeftButtonDown -= NotesPanel_PreviewMouseLeftButtonDown;
        NotesPanel.PreviewMouseMove -= NotesPanel_PreviewMouseMove;
        NotesPanel.PreviewMouseLeftButtonUp -= NotesPanel_PreviewMouseLeftButtonUp;


        MainCanvas.MouseLeftButtonDown -= MainCanvas_MouseLeftButtonDown;
        PreviewMouseDown -= MainWindow_PreviewMouseDown;
        KeyDown -= MainWindow_KeyDown;

        HeaderText.MouseLeftButtonDown -= HeaderText_MouseLeftButtonDown;
        HeaderTextEdit.LostFocus -= HeaderEditBox_LostFocus;
        HeaderTextEdit.KeyDown -= HeaderEditBox_KeyDown;
        SearchBox.TextChanged -= SearchBox_TextChanged;
        SearchBox.GotFocus -= SearchBox_GotFocus;
        SearchBox.LostFocus -= SearchBox_LostFocus;
        _noteEditor.NewNoteRequested -= NoteEditor_NewNoteRequested;
        _noteEditor.DeleteNoteRequested -= NoteEditor_DeleteNoteRequested;
        _noteEditor.NoteRenameRequested -= NoteEditor_NoteRenameRequested;
        _viewModel.PropertyChanged -= OnViewModelFilterTextChanged;

        _viewModel.NotesLoaded -= OnNotesLoaded;
        _viewModel.Dispose();
    }
}
