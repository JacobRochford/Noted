using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class App : Application
{
    static App()
    {
        // Draw TextBox selections behind the glyphs so SelectionTextBrush is honored.
        AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
    }

    private const string SingleInstanceMutexName = "Noted.SingleInstance";
    private static readonly TimeSpan RecentBackupInterval = TimeSpan.FromHours(24);
    private Mutex? _singleInstanceMutex;
    private AppSettingsService? _settingsService;
    private AppThemeManager? _themeManager;
    private FileOpenRequestService? _fileOpenRequestService;
    private NoteFileService? _fileService;
    private readonly ShutdownFlushCoordinator _shutdownFlushCoordinator = new();
    private readonly ApplicationShutdownSequence _shutdownSequence;
    private IDisposable? _checklistBackupRegistration;
    private IDisposable? _dictionaryBackupRegistration;
    private IDisposable? _scratchpadBackupRegistration;
    private IDisposable? _miniPadBackupRegistration;
    private IDisposable? _noteEditorBackupRegistration;
    private IMiniPadRecoveryService? _miniPadRecoveryService;
    private BackupCoordinator? _backupCoordinator;
    private bool _isShuttingDown;
    private string? _lastShutdownWarning;
    private int _fatalErrorShown;

    public App()
    {
        _shutdownSequence = new ApplicationShutdownSequence(_shutdownFlushCoordinator);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Check for another instance before base.OnStartup creates the main window
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                var requestedFile = e.Args.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(requestedFile) &&
                    FileOpenRequestService.TrySend(requestedFile))
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    Shutdown();
                    return;
                }

                AppDialog.Show(
                    "Noted is already running.\n\nCheck your system tray to find the existing instance.",
                    "Noted Already Running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A mutex with this name exists in a different security context —
            // treat it as another instance already running.
            AppDialog.Show(
                "Noted is already running.\n\nCheck your system tray to find the existing instance.",
                "Noted Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        NoteFileService? startupFileService = null;
        NoteEditorWindow? startupNoteEditor = null;

        try
        {
            var appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Noted");
            var fullBackupService = new FullBackupService(
                appDataDirectory,
                appDataDirectory);
            var backupCoordinator = new BackupCoordinator(
                fullBackupService,
                _shutdownFlushCoordinator,
                () => _fileService?.NotesDirectory);
            _backupCoordinator = backupCoordinator;
            var pendingRestoreResult = backupCoordinator.ApplyPendingRestore();
            if (pendingRestoreResult.Status == FullBackupStatus.Failed)
                throw new IOException(pendingRestoreResult.Message);

            base.OnStartup(e);
            TryCreateDesktopShortcut();

            var settings = new AppSettingsService();
            WindowAppearance.Initialize(settings);
            settings.PersistenceWarning += Settings_PersistenceWarning;
            _settingsService = settings;
            var themeManager = new AppThemeManager(
                Resources,
                settings.LoadAppThemeMode(),
                settings.LoadAccentColor());
            _themeManager = themeManager;
            _miniPadRecoveryService = new MiniPadRecoveryService(settings.AppDataDirectory);
            var fileService = new NoteFileService(settings);
            startupFileService = fileService;
            fullBackupService.UpdateNotesDirectory(fileService.NotesDirectory);
            var noteEditor = new NoteEditorWindow(
                new NoteContentService(settings.AppDataDirectory),
                settings,
                new NoteRecoveryService(settings.AppDataDirectory),
                new NoteEditorSessionService(settings.AppDataDirectory));
            _noteEditorBackupRegistration = backupCoordinator.RegisterPreparationParticipant(
                "Note editor",
                BackupPreparationPhase.AfterSharedFlush,
                () => CreatePersistenceSaveResult(
                    noteEditor.TryFlushForBackup(out var error),
                    error,
                    "Note editor recovery data could not be saved."),
                () => noteEditor.BackupBlockingIssue,
                () => noteEditor.RecoveryBlocksBackup,
                "Some automatically recovered notes still need to be saved, or note recovery reported a problem.",
                includeRecoveryInUserBackup: true);
            startupNoteEditor = noteEditor;
            var noteOperations = new NoteOperations(fileService, noteEditor.Workspace);
            var mainWindow = new MainWindow(
                settings,
                fileService,
                noteEditor,
                noteOperations,
                new RunOnStartupService(),
                backupCoordinator,
                themeManager.Apply);
            _fileService = fileService;
            MainWindow = mainWindow;
            mainWindow.Closing += MainWindow_Closing;
            backupCoordinator.ShutdownRequested += BackupCoordinator_ShutdownRequested;

            WindowManager.Main = mainWindow;
            WindowManager.ChecklistProvider = GetOrCreateChecklistWindow;
            WindowManager.DictionaryProvider = GetOrCreateDictionaryWindow;
            WindowManager.ScratchpadProvider = GetOrCreateScratchpadWindow;
            WindowManager.MiniPadProvider = GetOrCreateMiniPadWindow;
            WindowManager.Editor = noteEditor;

            _fileOpenRequestService = new FileOpenRequestService(
            filePath =>
            {
                if (!_isShuttingDown)
                    noteEditor.OpenNote(filePath);
            },
            action =>
            {
                if (!_isShuttingDown)
                    Dispatcher.BeginInvoke(action);
            });
            ObserveBackgroundTask(_fileOpenRequestService.Start());

            mainWindow.Show();
            foreach (var requestedFile in e.Args.Where(path => !string.IsNullOrWhiteSpace(path)))
                noteEditor.OpenNote(requestedFile);
            if (pendingRestoreResult.Status == FullBackupStatus.Restored)
            {
                var restoreMessage = string.IsNullOrWhiteSpace(pendingRestoreResult.Warning)
                    ? pendingRestoreResult.Message
                    : $"{pendingRestoreResult.Message}\n\n{pendingRestoreResult.Warning}";
                AppDialog.Show(
                    restoreMessage,
                    "Noted - Backup Restored",
                    MessageBoxButton.OK,
                    string.IsNullOrWhiteSpace(pendingRestoreResult.Warning)
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }
            if (settings.RecoveryNotice is not null)
            {
                backupCoordinator.RecordRunRecoveryIssue("Settings recovery was used during this application run.");
                AppDialog.Show(
                    settings.RecoveryNotice,
                    "Noted - Settings Recovered",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (settings.LoadChecklistWindowState().ReopenOnStartup)
                WindowManager.ShowChecklist();
            if (settings.LoadDictionaryWindowState().ReopenOnStartup)
                WindowManager.ShowDictionary();
            if (settings.LoadScratchpadWindowState().ReopenOnStartup)
                WindowManager.ShowScratchpad();

            noteEditor.ReopenSavedTabs();
            if (noteEditor.RecoveryBlocksBackup)
                backupCoordinator.RecordRunRecoveryIssue("Note editor recovery was used or reported a problem during this application run.");
            RestoreMiniPadWindow();
            ObserveBackgroundTask(UpdateService.CheckForUpdatesAsync());
        }
        catch (Exception ex)
        {
            if (WindowManager.Main is null)
            {
                CloseExistingWindow(startupNoteEditor, "note editor");
                try { startupFileService?.Dispose(); } catch (Exception cleanupException) { ExceptionDiagnostics.Record(cleanupException); }
            }

            HandleFatalStartupFailure(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;

        try
        {

            var mainWindow = WindowManager.Main;
            if (mainWindow is not null)
            {
                mainWindow.Closing -= MainWindow_Closing;
            }

            if (WindowManager.Scratchpad is { } scratchpadWindow)
            {
                if (!scratchpadWindow.TryFlushPendingContent(out var error))
                {
                    AppDialog.Show(
                        error ?? "Scratchpad content could not be saved before shutdown.",
                        "Scratchpad Save Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            CloseExistingWindow(WindowManager.Checklist, "Checklist");
            CloseExistingWindow(WindowManager.Dictionary, "Dictionary");
            CloseExistingWindow(WindowManager.Scratchpad, "Scratchpad");
            CloseExistingWindow(WindowManager.Editor, "note editor");
            CloseMiniPadWindow();

            _checklistBackupRegistration?.Dispose();
            _checklistBackupRegistration = null;
            _dictionaryBackupRegistration?.Dispose();
            _dictionaryBackupRegistration = null;
            _scratchpadBackupRegistration?.Dispose();
            _scratchpadBackupRegistration = null;
            _miniPadBackupRegistration?.Dispose();
            _miniPadBackupRegistration = null;
            _noteEditorBackupRegistration?.Dispose();
            _noteEditorBackupRegistration = null;

            mainWindow?.CleanupResources();
            _themeManager?.Dispose();
            _themeManager = null;
            _fileOpenRequestService?.Dispose();
            _fileOpenRequestService = null;
            try { _fileService?.Dispose(); } catch (Exception ex) { ExceptionDiagnostics.Record(ex); }
            WindowManager.ClearAll();

            if (_backupCoordinator?.HasRequestedRestore == true)
            {
                var restoreResult = _backupCoordinator.ApplyPendingRestore();
                var restoreDetails = string.IsNullOrWhiteSpace(restoreResult.Warning)
                    ? restoreResult.Message
                    : $"{restoreResult.Message}\n\n{restoreResult.Warning}";
                var message = restoreResult.Status == FullBackupStatus.Restored
                    ? $"{restoreDetails}\n\nNoted will now close. Open it again to load the restored data."
                    : $"{restoreDetails}\n\nNoted will retry before loading data the next time it starts.";
                AppDialog.Show(
                    message,
                    restoreResult.Status == FullBackupStatus.Restored
                        ? "Noted - Backup Restored"
                        : "Noted - Restore Incomplete",
                    MessageBoxButton.OK,
                    restoreResult.Status == FullBackupStatus.Restored &&
                    string.IsNullOrWhiteSpace(restoreResult.Warning)
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }

            _fileService = null;
            _miniPadRecoveryService = null;
            if (_backupCoordinator is not null)
                _backupCoordinator.ShutdownRequested -= BackupCoordinator_ShutdownRequested;
            _backupCoordinator = null;
        }
        finally
        {
            try
            {
                ReleaseSingleInstanceMutex();
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }

    private ChecklistWindow? GetOrCreateChecklistWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return WindowManager.Checklist;

        if (WindowManager.Checklist is not null)
            return WindowManager.Checklist;

        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var window = new ChecklistWindow(
            settings,
            new ChecklistContentService(settings.AppDataDirectory));
        var backupCoordinator = _backupCoordinator
            ?? throw new InvalidOperationException("Backup coordination is not initialized.");
        _checklistBackupRegistration = backupCoordinator.RegisterPreparationParticipant(
            "Checklist",
            BackupPreparationPhase.SharedFlush,
            () =>
            {
                var success = window.TryFlushPendingContent(out var error);
                return success
                    ? PersistenceSaveResult.Succeeded()
                    : PersistenceSaveResult.Failed(
                        error ?? "Checklist content could not be saved before shutdown.");
            },
            () => window.BackupBlockingIssue,
            () => window.RecoveryBlocksBackup,
            "Checklist recovery reported a problem during this application run.");
        window.Closed += ChecklistWindow_Closed;
        WindowManager.Checklist = window;
        return window;
    }

    private DictionaryWindow? GetOrCreateDictionaryWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return WindowManager.Dictionary;

        if (WindowManager.Dictionary is not null)
            return WindowManager.Dictionary;

        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var window = new DictionaryWindow(
            settings,
            new DictionaryContentService(settings.AppDataDirectory));
        var backupCoordinator = _backupCoordinator
            ?? throw new InvalidOperationException("Backup coordination is not initialized.");
        _dictionaryBackupRegistration = backupCoordinator.RegisterPreparationParticipant(
            "Dictionary",
            BackupPreparationPhase.SharedFlush,
            () =>
            {
                var success = window.TryFlushPendingContent(out var error);
                return success
                    ? PersistenceSaveResult.Succeeded()
                    : PersistenceSaveResult.Failed(
                        error ?? "Dictionary content could not be saved before shutdown.");
            },
            () => window.BackupBlockingIssue,
            () => window.RecoveryBlocksBackup,
            "Dictionary recovery reported a problem during this application run.");
        window.Closed += DictionaryWindow_Closed;
        WindowManager.Dictionary = window;
        return window;
    }

    private ScratchpadWindow? GetOrCreateScratchpadWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return WindowManager.Scratchpad;

        if (WindowManager.Scratchpad is not null)
            return WindowManager.Scratchpad;

        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var contentService = new ScratchpadContentService(settings.AppDataDirectory);
        var viewModel = new ScratchpadWindowViewModel(settings, contentService);
        var window = new ScratchpadWindow(viewModel);
        var backupCoordinator = _backupCoordinator
            ?? throw new InvalidOperationException("Backup coordination is not initialized.");
        _scratchpadBackupRegistration = backupCoordinator.RegisterPreparationParticipant(
            "Scratchpad",
            BackupPreparationPhase.BeforeSharedFlush,
            () => CreatePersistenceSaveResult(
                window.TryFlushPendingContent(out var error),
                error,
                "Scratchpad content could not be saved."),
            () => window.BackupBlockingIssue,
            () => window.RecoveryBlocksBackup,
            "Scratchpad recovery reported a problem during this application run.",
            preparationOrder: 1);
        window.Closed += ScratchpadWindow_Closed;
        WindowManager.Scratchpad = window;
        return window;
    }

    private void ChecklistWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not ChecklistWindow window)
            return;

        window.Closed -= ChecklistWindow_Closed;
        if (!ReferenceEquals(WindowManager.Checklist, window))
            return;

        _checklistBackupRegistration?.Dispose();
        _checklistBackupRegistration = null;
        WindowManager.Checklist = null;
    }

    private void DictionaryWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not DictionaryWindow window)
            return;

        window.Closed -= DictionaryWindow_Closed;
        if (!ReferenceEquals(WindowManager.Dictionary, window))
            return;

        _dictionaryBackupRegistration?.Dispose();
        _dictionaryBackupRegistration = null;
        WindowManager.Dictionary = null;
    }

    private void ScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not ScratchpadWindow window)
            return;

        window.Closed -= ScratchpadWindow_Closed;
        if (!ReferenceEquals(WindowManager.Scratchpad, window))
            return;

        _scratchpadBackupRegistration?.Dispose();
        _scratchpadBackupRegistration = null;
        WindowManager.Scratchpad = null;
    }

    private MiniPadWindow? GetOrCreateMiniPadWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return WindowManager.MiniPad;

        if (WindowManager.MiniPad is not null)
            return WindowManager.MiniPad;

        var recoveryService = _miniPadRecoveryService
            ?? throw new InvalidOperationException("MiniPad recovery is not initialized.");
        return CreateMiniPadWindow(recoveryService, null);
    }

    private void Settings_PersistenceWarning(string warning)
    {
        _backupCoordinator?.RecordRunRecoveryIssue(
            "Settings recovery protection reported a warning during this application run.");
        if (_isShuttingDown)
            return;

        AppDialog.Show(
            $"Your settings were saved, but recovery protection needs attention.\n\n{warning}",
            "Noted - Settings Recovery Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RestoreMiniPadWindow()
    {
        var recoveryService = _miniPadRecoveryService
            ?? throw new InvalidOperationException("MiniPad recovery is not initialized.");

        var loadResult = recoveryService.LoadDraft();
        var shouldReopen = _settingsService?.LoadMiniPadWindowState().ReopenOnStartup == true;
        if (loadResult.Draft is not null || shouldReopen)
        {
            var window = CreateMiniPadWindow(recoveryService, loadResult.Draft);
            if (shouldReopen || loadResult.Issues.Count > 0)
                window.Show();
        }

        if (loadResult.Issues.Count == 0)
            return;

        _backupCoordinator?.RecordRunRecoveryIssue(
            "MiniPad recovery reported a problem during this application run.");

        const int maximumDisplayedIssues = 5;
        var issueText = string.Join(
            "\n\n",
            loadResult.Issues
                .Take(maximumDisplayedIssues)
                .Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"));
        var remainingCount = loadResult.Issues.Count - maximumDisplayedIssues;
        var remainingText = remainingCount > 0
            ? $"\n\n{remainingCount} additional recovery issue(s) were not shown."
            : string.Empty;
        AppDialog.Show(
            $"Some MiniPad recovery files need attention. No recoverable content was silently discarded.\n\n{issueText}{remainingText}",
            "MiniPad Recovery",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private MiniPadWindow CreateMiniPadWindow(
        IMiniPadRecoveryService recoveryService,
        MiniPadRecoveryDraft? draft)
    {
        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var window = new MiniPadWindow(recoveryService, settings, draft);
        var backupCoordinator = _backupCoordinator
            ?? throw new InvalidOperationException("Backup coordination is not initialized.");
        _miniPadBackupRegistration = backupCoordinator.RegisterPreparationParticipant(
            "MiniPad",
            BackupPreparationPhase.BeforeSharedFlush,
            () => CreatePersistenceSaveResult(
                window.TryFlushPendingContent(out var error),
                error,
                "MiniPad content could not be saved."),
            () => window.BackupBlockingIssue,
            preparationOrder: 0);
        window.Closed += MiniPadWindow_Closed;
        WindowManager.MiniPad = window;
        return window;
    }

    private void MiniPadWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not MiniPadWindow window)
            return;

        window.Closed -= MiniPadWindow_Closed;
        if (!ReferenceEquals(WindowManager.MiniPad, window))
            return;

        _miniPadBackupRegistration?.Dispose();
        _miniPadBackupRegistration = null;
        WindowManager.MiniPad = null;
    }

    private void CloseMiniPadWindow()
    {
        var window = WindowManager.MiniPad;
        if (window is null)
            return;

        window.Closed -= MiniPadWindow_Closed;
        _miniPadBackupRegistration?.Dispose();
        _miniPadBackupRegistration = null;
        if (!window.TryFlushPendingContent(out var error))
        {
            AppDialog.Show(
                error ?? "MiniPad recovery data could not be saved before shutdown.",
                "MiniPad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        window.KeepDraftOnClose();
        CloseExistingWindow(window, "MiniPad");
        WindowManager.MiniPad = null;
    }

    private static void CloseExistingWindow(Window? window, string name)
    {
        if (window is null)
            return;

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            ExceptionDiagnostics.Record(ex, $"Close {name} window");
        }
    }

    private void BackupCoordinator_ShutdownRequested(object? sender, EventArgs e) =>
        WindowManager.Main?.Close();

    private static PersistenceSaveResult CreatePersistenceSaveResult(
        bool success,
        string? error,
        string fallbackError) =>
        success
            ? PersistenceSaveResult.Succeeded()
            : PersistenceSaveResult.Failed(error ?? fallbackError);

    private void TryUpdateRecentBackup()
    {
        var result = _backupCoordinator?.TryUpdateRecentBackup(RecentBackupInterval);
        if (result is null)
            return;
        if ((result.Status is FullBackupStatus.Created or FullBackupStatus.Skipped) &&
            string.IsNullOrWhiteSpace(result.Warning))
        {
            return;
        }

        var warning = string.IsNullOrWhiteSpace(result.Warning)
            ? result.Message
            : $"{result.Message}\n\n{result.Warning}";
        AppDialog.Show(
            $"Noted kept the existing backup copies unchanged.\n\n{warning}",
            "Backup Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        var directFlushes = new List<ShutdownDirectFlush>();
        if (WindowManager.MiniPad is { } miniPadWindow)
        {
            directFlushes.Add(new ShutdownDirectFlush(
                "MiniPad",
                () => CreatePersistenceSaveResult(
                    miniPadWindow.TryFlushPendingContent(out var error),
                    error,
                    "MiniPad recovery data could not be saved.")));
        }
        if (WindowManager.Scratchpad is { } scratchpadWindow)
        {
            directFlushes.Add(new ShutdownDirectFlush(
                "Scratchpad",
                () => CreatePersistenceSaveResult(
                    scratchpadWindow.TryFlushPendingContent(out var error),
                    error,
                    "Scratchpad content could not be saved before shutdown.")));
        }

        var result = _shutdownSequence.Execute(
            directFlushes,
            () => WindowManager.TryPrepareForShutdown(out var error)
                ? WindowPreparationResult.Succeeded()
                : WindowPreparationResult.Failed(error),
            () => _backupCoordinator?.HasRequestedRestore == true,
            () => _backupCoordinator?.PrepareRequestedRestore()
                ?? throw new InvalidOperationException("Backup coordination is not initialized."),
            CancelRequestedBackupRestore,
            TryUpdateRecentBackup);

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            AppDialog.Show(
                result.Warning,
                "Noted - Backup Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (result.CanShutdown)
        {
            _lastShutdownWarning = null;
            return;
        }

        e.Cancel = true;
        ShowShutdownBlocked(result);
    }

    private void ShowShutdownBlocked(ApplicationShutdownResult result)
    {
        if (result.BlockReason == ShutdownBlockReason.DirectFlush)
        {
            var isMiniPad = string.Equals(result.ParticipantName, "MiniPad", StringComparison.Ordinal);
            var message = result.Error ?? (isMiniPad
                ? "MiniPad recovery data could not be saved."
                : "Scratchpad content could not be saved before shutdown.");
            if (message == _lastShutdownWarning)
                return;

            _lastShutdownWarning = message;
            AppDialog.Show(
                isMiniPad
                    ? $"Noted could not close because MiniPad recovery data was not saved.\n\n{message}\n\nThe application will remain open so you can retry."
                    : $"Noted could not close because pending Scratchpad content was not saved.\n\n{message}\n\nThe application will remain open so you can retry.",
                isMiniPad ? "MiniPad Save Failed" : "Scratchpad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (result.BlockReason == ShutdownBlockReason.SharedFlush)
        {
            var message = string.Join(
                "\n",
                result.SharedFlushFailures!.Select(failure =>
                    $"{failure.ParticipantName}: {failure.Error}"));
            if (message == _lastShutdownWarning)
                return;

            _lastShutdownWarning = message;
            AppDialog.Show(
                $"Noted could not close because tool content was not saved.\n\n{message}\n\nThe application will remain open so you can retry.",
                "Tool Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _lastShutdownWarning = null;
        if (result.BlockReason == ShutdownBlockReason.WindowPreparation)
        {
            if (result.Error is null)
                return;
            AppDialog.Show(
                $"Noted could not close because window state was not saved.\n\n{result.Error}\n\nThe application will remain open so you can retry.",
                "Window State Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (result.BlockReason == ShutdownBlockReason.RestoreProtection)
        {
            AppDialog.Show(
                $"Noted did not start the restore because the current data could not be protected first.\n\n{result.Error}",
                "Noted - Restore Not Started",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AppDialog.Show(
            result.Error ?? "The requested restore could not be scheduled.",
            "Noted - Restore Not Started",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void CancelRequestedBackupRestore()
    {
        WindowManager.CancelPreparedClose();
        _backupCoordinator?.CancelRequestedRestore();
    }

    // Expected settings failures are recoverable after actionable feedback.
    // Every other unhandled dispatcher exception remains fatal.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is SettingsPersistenceException)
        {
            ExceptionDiagnostics.Record(e.Exception);
            e.Handled = true;
            AppDialog.Show(
                e.Exception.Message,
                "Noted - Settings Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ShowFatalError("An unexpected application error occurred.", e.Exception);
        ReleaseSingleInstanceMutex();
    }

    // last chance to log an unhandled exception that may have occurred off the UI thread
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown error");
        ExceptionDiagnostics.Record(exception, "Fatal background error");
    }

    // log task exceptions that were never observed elsewhere
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ExceptionDiagnostics.Record(e.Exception);
        e.SetObserved();
    }

    // observe background tasks and let unexpected failures reach WPF's dispatcher handler
    internal static async void ObserveBackgroundTask(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            ExceptionDiagnostics.Record(ex);
            throw;
        }
    }

    private void HandleFatalStartupFailure(Exception exception)
    {
        _isShuttingDown = true;
        try
        {
            ShowFatalError("Noted could not start.", exception);
        }
        finally
        {
            // Release before requesting shutdown so an immediate relaunch is
            // not blocked even if cleanup encounters another failure.
            ReleaseSingleInstanceMutex();
            Shutdown(-1);
        }
    }

    private void ShowFatalError(string context, Exception exception)
    {
        ExceptionDiagnostics.Record(exception, context);
        if (Interlocked.Exchange(ref _fatalErrorShown, 1) != 0)
            return;

        var message = exception is SettingsPersistenceException
            ? exception.Message
            : $"{context}\n\nDiagnostic details were sent to the application log.";

        AppDialog.Show(
            $"{message}\n\nNoted must close.",
            "Noted - Fatal Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void ReleaseSingleInstanceMutex()
    {
        var mutex = _singleInstanceMutex;
        if (mutex is null)
            return;

        _singleInstanceMutex = null;
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            ExceptionDiagnostics.Record(ex, "Release single-instance mutex");
        }
        finally
        {
            mutex.Dispose();
        }
    }

    private static void TryCreateDesktopShortcut()
    {
        try
        {
            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Noted.lnk");

            if (File.Exists(shortcutPath))
                return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = Environment.ProcessPath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Noted.exe");
            shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            shortcut.Save();
        }
        catch (Exception ex) when (FileSystemErrors.IsExpected(ex) ||
                                   ex is System.Runtime.InteropServices.COMException)
        {
            ExceptionDiagnostics.Record(ex);
        }
    }
}
