using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Noted.SingleInstance";
    private static readonly TimeSpan RecentBackupInterval = TimeSpan.FromHours(24);
    private Mutex? _singleInstanceMutex;
    private AppSettingsService? _settingsService;
    private NoteFileService? _fileService;
    private MainWindow? _mainWindow;
    private NoteEditorWindow? _noteEditorWindow;
    private ChecklistWindow? _checklistWindow;
    private DictionaryWindow? _dictionaryWindow;
    private ScratchpadWindow? _scratchpadWindow;
    private readonly ShutdownFlushCoordinator _shutdownFlushCoordinator = new();
    private IDisposable? _checklistShutdownRegistration;
    private IDisposable? _dictionaryShutdownRegistration;
    private MiniPadWindow? _miniPadWindow;
    private IMiniPadRecoveryService? _miniPadRecoveryService;
    private FullBackupService? _fullBackupService;
    private readonly HashSet<string> _backupBlockReasons = new(StringComparer.Ordinal);
    private bool _restoreUserBackupOnShutdown;
    private bool _isShuttingDown;
    private string? _lastShutdownWarning;
    private int _fatalErrorShown;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Check for an existing instance before WPF creates the main window.
        // base.OnStartup processes StartupUri, so returning before it prevents
        // the window from appearing in the duplicate-instance case.
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
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
            var pendingRestoreResult = fullBackupService.ApplyPendingUserBackupRestore();
            if (pendingRestoreResult.Status == FullBackupStatus.Failed)
                throw new IOException(pendingRestoreResult.Message);

            base.OnStartup(e);
            TryCreateDesktopShortcut();

            var settings = new AppSettingsService();
            settings.PersistenceWarning += Settings_PersistenceWarning;
            _settingsService = settings;
            _miniPadRecoveryService = new MiniPadRecoveryService(settings.AppDataDirectory);
            var fileService = new NoteFileService(settings);
            startupFileService = fileService;
            fullBackupService.UpdateNotesDirectory(fileService.NotesDirectory);
            _fullBackupService = fullBackupService;
            var noteEditor = new NoteEditorWindow(
                new NoteContentService(
                    () => fileService.NotesDirectory,
                    settings.AppDataDirectory),
                settings,
                new NoteRecoveryService(settings.AppDataDirectory),
                new NoteEditorSessionService(settings.AppDataDirectory));
            startupNoteEditor = noteEditor;
            var mainWindow = new MainWindow(
                settings,
                fileService,
                noteEditor,
                new RunOnStartupService(),
                CreateOrUpdateUserBackup,
                GetUserBackupInfo,
                RequestUserBackupRestore);
            _fileService = fileService;
            _noteEditorWindow = noteEditor;
            _mainWindow = mainWindow;
            MainWindow = mainWindow;
            mainWindow.Closing += MainWindow_Closing;

            WindowManager.Main = mainWindow;
            WindowManager.ChecklistProvider = GetOrCreateChecklistWindow;
            WindowManager.DictionaryProvider = GetOrCreateDictionaryWindow;
            WindowManager.ScratchpadProvider = GetOrCreateScratchpadWindow;
            WindowManager.MiniPadProvider = GetOrCreateMiniPadWindow;
            WindowManager.Editor = noteEditor;

            mainWindow.Show();
            if (pendingRestoreResult.Status == FullBackupStatus.Restored)
            {
                AppDialog.Show(
                    pendingRestoreResult.Message,
                    "Noted - Backup Restored",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            if (settings.RecoveryNotice is not null)
            {
                BlockBackupsForThisRun("Settings recovery was used during this application run.");
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
                BlockBackupsForThisRun("Note editor recovery was used or reported a problem during this application run.");
            RestoreMiniPadWindow();
            _ = UpdateService.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            if (_mainWindow is null)
            {
                CloseExistingWindow(startupNoteEditor, "note editor");
                try { startupFileService?.Dispose(); } catch (Exception cleanupException) { Debug.WriteLine(cleanupException); }
            }

            HandleFatalStartupFailure(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;

        try
        {

            var mainWindow = _mainWindow;
            if (mainWindow is not null)
            {
                mainWindow.Closing -= MainWindow_Closing;
            }

            if (_scratchpadWindow is not null)
            {
                if (!_scratchpadWindow.TryFlushPendingContent(out var error))
                {
                    AppDialog.Show(
                        error ?? "Scratchpad content could not be saved before shutdown.",
                        "Scratchpad Save Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            CloseExistingWindow(_checklistWindow, "Checklist");
            CloseExistingWindow(_dictionaryWindow, "Dictionary");
            CloseExistingWindow(_scratchpadWindow, "Scratchpad");
            CloseExistingWindow(_noteEditorWindow, "note editor");
            CloseMiniPadWindow();

            _checklistShutdownRegistration?.Dispose();
            _checklistShutdownRegistration = null;
            _dictionaryShutdownRegistration?.Dispose();
            _dictionaryShutdownRegistration = null;

            mainWindow?.CleanupResources();
            try { _fileService?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            WindowManager.ClearAll();

            if (_restoreUserBackupOnShutdown && _fullBackupService is not null)
            {
                var restoreResult = _fullBackupService.ApplyPendingUserBackupRestore();
                var message = restoreResult.Status == FullBackupStatus.Restored
                    ? $"{restoreResult.Message}\n\nNoted will now close. Open it again to load the restored data."
                    : $"{restoreResult.Message}\n\nNoted will retry before loading data the next time it starts.";
                AppDialog.Show(
                    message,
                    restoreResult.Status == FullBackupStatus.Restored
                        ? "Noted - Backup Restored"
                        : "Noted - Restore Incomplete",
                    MessageBoxButton.OK,
                    restoreResult.Status == FullBackupStatus.Restored
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
            }

            _fileService = null;
            _mainWindow = null;
            _noteEditorWindow = null;
            _checklistWindow = null;
            _dictionaryWindow = null;
            _scratchpadWindow = null;
            _miniPadWindow = null;
            _miniPadRecoveryService = null;
            _fullBackupService = null;
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
            return _checklistWindow;

        if (_checklistWindow is not null)
            return _checklistWindow;

        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var window = new ChecklistWindow(
            settings,
            new ChecklistContentService(settings.AppDataDirectory, settings));
        _checklistShutdownRegistration = _shutdownFlushCoordinator.Register(
            "Checklist",
            () =>
            {
                var success = window.TryFlushPendingContent(out var error);
                return success
                    ? PersistenceSaveResult.Succeeded()
                    : PersistenceSaveResult.Failed(
                        error ?? "Checklist content could not be saved before shutdown.");
            });
        window.Closed += ChecklistWindow_Closed;
        _checklistWindow = window;
        WindowManager.Checklist = window;
        return window;
    }

    private DictionaryWindow? GetOrCreateDictionaryWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return _dictionaryWindow;

        if (_dictionaryWindow is not null)
            return _dictionaryWindow;

        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var window = new DictionaryWindow(
            settings,
            new DictionaryContentService(settings.AppDataDirectory, settings));
        _dictionaryShutdownRegistration = _shutdownFlushCoordinator.Register(
            "Dictionary",
            () =>
            {
                var success = window.TryFlushPendingContent(out var error);
                return success
                    ? PersistenceSaveResult.Succeeded()
                    : PersistenceSaveResult.Failed(
                        error ?? "Dictionary content could not be saved before shutdown.");
            });
        window.Closed += DictionaryWindow_Closed;
        _dictionaryWindow = window;
        WindowManager.Dictionary = window;
        return window;
    }

    private ScratchpadWindow? GetOrCreateScratchpadWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return _scratchpadWindow;

        if (_scratchpadWindow is not null)
            return _scratchpadWindow;

        var settings = _settingsService
            ?? throw new InvalidOperationException("Application settings are not initialized.");
        var contentService = new ScratchpadContentService(settings.AppDataDirectory);
        var viewModel = new ScratchpadWindowViewModel(settings, contentService);
        var window = new ScratchpadWindow(viewModel);
        window.Closed += ScratchpadWindow_Closed;
        _scratchpadWindow = window;
        WindowManager.Scratchpad = window;
        return window;
    }

    private void ChecklistWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not ChecklistWindow window)
            return;

        window.Closed -= ChecklistWindow_Closed;
        if (!ReferenceEquals(_checklistWindow, window))
            return;

        _checklistShutdownRegistration?.Dispose();
        _checklistShutdownRegistration = null;
        _checklistWindow = null;
        if (ReferenceEquals(WindowManager.Checklist, window))
            WindowManager.Checklist = null;
    }

    private void DictionaryWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not DictionaryWindow window)
            return;

        window.Closed -= DictionaryWindow_Closed;
        if (!ReferenceEquals(_dictionaryWindow, window))
            return;

        _dictionaryShutdownRegistration?.Dispose();
        _dictionaryShutdownRegistration = null;
        _dictionaryWindow = null;
        if (ReferenceEquals(WindowManager.Dictionary, window))
            WindowManager.Dictionary = null;
    }

    private void ScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not ScratchpadWindow window)
            return;

        window.Closed -= ScratchpadWindow_Closed;
        if (!ReferenceEquals(_scratchpadWindow, window))
            return;

        _scratchpadWindow = null;
        if (ReferenceEquals(WindowManager.Scratchpad, window))
            WindowManager.Scratchpad = null;
    }

    private MiniPadWindow? GetOrCreateMiniPadWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return _miniPadWindow;

        if (_miniPadWindow is not null)
            return _miniPadWindow;

        var recoveryService = _miniPadRecoveryService
            ?? throw new InvalidOperationException("MiniPad recovery is not initialized.");
        return CreateMiniPadWindow(recoveryService, null);
    }

    private void Settings_PersistenceWarning(string warning)
    {
        BlockBackupsForThisRun("Settings recovery protection reported a warning during this application run.");
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

        BlockBackupsForThisRun("MiniPad recovery reported a problem during this application run.");

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
        window.Closed += MiniPadWindow_Closed;
        _miniPadWindow = window;
        WindowManager.MiniPad = window;
        return window;
    }

    private void MiniPadWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not MiniPadWindow window)
            return;

        window.Closed -= MiniPadWindow_Closed;
        if (!ReferenceEquals(_miniPadWindow, window))
            return;

        _miniPadWindow = null;
        if (ReferenceEquals(WindowManager.MiniPad, window))
            WindowManager.MiniPad = null;
    }

    private void CloseMiniPadWindow()
    {
        var window = _miniPadWindow;
        if (window is null)
            return;

        window.Closed -= MiniPadWindow_Closed;
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
        _miniPadWindow = null;
        if (ReferenceEquals(WindowManager.MiniPad, window))
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
            Debug.WriteLine($"Failed to close the {name} window: {ex}");
        }
    }

    private FullBackupInfo GetUserBackupInfo() =>
        _fullBackupService?.GetUserBackupInfo()
        ?? new FullBackupInfo(false, false, null, 0, "Backups are not available.");

    private FullBackupResult CreateOrUpdateUserBackup()
    {
        var service = _fullBackupService;
        if (service is null)
        {
            return new FullBackupResult(
                FullBackupStatus.Failed,
                FullBackupType.User,
                null,
                "Backups are not available.");
        }

        var preparationIssues = PrepareDataForBackup(includeRunRecoveryHistory: false);
        if (preparationIssues.Count > 0)
            return CreateBlockedBackupResult(FullBackupType.User, preparationIssues);

        if (_fileService is not null)
            service.UpdateNotesDirectory(_fileService.NotesDirectory);
        return service.CreateOrUpdateUserBackup();
    }

    private void RequestUserBackupRestore()
    {
        _restoreUserBackupOnShutdown = true;
        _mainWindow?.Close();
    }

    private IReadOnlyList<string> PrepareDataForBackup(bool includeRunRecoveryHistory)
    {
        var issues = includeRunRecoveryHistory
            ? new List<string>(_backupBlockReasons)
            : [];

        if (_miniPadWindow is not null)
        {
            if (!_miniPadWindow.TryFlushPendingContent(out var error))
                issues.Add(error ?? "MiniPad content could not be saved.");
            if (!string.IsNullOrWhiteSpace(_miniPadWindow.BackupBlockingIssue))
                issues.Add(_miniPadWindow.BackupBlockingIssue!);
        }

        if (_scratchpadWindow is not null)
        {
            if (!_scratchpadWindow.TryFlushPendingContent(out var error))
                issues.Add(error ?? "Scratchpad content could not be saved.");
            if (!string.IsNullOrWhiteSpace(_scratchpadWindow.BackupBlockingIssue))
                issues.Add(_scratchpadWindow.BackupBlockingIssue!);
            if (includeRunRecoveryHistory && _scratchpadWindow.RecoveryBlocksBackup)
                issues.Add("Scratchpad recovery reported a problem during this application run.");
        }

        foreach (var failure in _shutdownFlushCoordinator.FlushAll())
            issues.Add($"{failure.ParticipantName}: {failure.Error}");

        if (!string.IsNullOrWhiteSpace(_checklistWindow?.BackupBlockingIssue))
            issues.Add(_checklistWindow.BackupBlockingIssue!);
        if (includeRunRecoveryHistory && _checklistWindow?.RecoveryBlocksBackup == true)
            issues.Add("Checklist recovery reported a problem during this application run.");
        if (!string.IsNullOrWhiteSpace(_dictionaryWindow?.BackupBlockingIssue))
            issues.Add(_dictionaryWindow.BackupBlockingIssue!);
        if (includeRunRecoveryHistory && _dictionaryWindow?.RecoveryBlocksBackup == true)
            issues.Add("Dictionary recovery reported a problem during this application run.");

        if (_noteEditorWindow is not null)
        {
            if (!_noteEditorWindow.TryFlushForBackup(out var error))
                issues.Add(error ?? "Note editor recovery data could not be saved.");
            if (_noteEditorWindow.RecoveryBlocksBackup)
                issues.Add("Some automatically recovered notes still need to be saved, or note recovery reported a problem.");
        }

        return issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static FullBackupResult CreateBlockedBackupResult(
        FullBackupType backupType,
        IReadOnlyList<string> issues)
    {
        const int maximumShownIssues = 5;
        var message = string.Join(" ", issues.Take(maximumShownIssues));
        if (issues.Count > maximumShownIssues)
            message += $" {issues.Count - maximumShownIssues} additional issue(s) were not shown.";

        return new FullBackupResult(
            FullBackupStatus.Blocked,
            backupType,
            null,
            backupType == FullBackupType.User
                ? $"The backup was not created because current data could not be prepared safely. {message}"
                : $"The recent automatic backup was not created because current data could not be prepared safely. {message}");
    }

    private void BlockBackupsForThisRun(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
            _backupBlockReasons.Add(reason);
    }

    private void TryUpdateRecentBackup()
    {
        var service = _fullBackupService;
        if (service is null || !service.UserBackupExists)
            return;

        var preparationIssues = PrepareDataForBackup(includeRunRecoveryHistory: true);
        if (preparationIssues.Count > 0)
            return;

        if (_fileService is not null)
            service.UpdateNotesDirectory(_fileService.NotesDirectory);
        var result = service.CreateRecentBackupIfDue(RecentBackupInterval);
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
        if (_miniPadWindow is not null &&
            !_miniPadWindow.TryFlushPendingContent(out var recoveryError))
        {
            e.Cancel = true;
            CancelRequestedUserBackupRestore();
            var message = recoveryError ?? "MiniPad recovery data could not be saved.";
            if (message == _lastShutdownWarning)
                return;

            _lastShutdownWarning = message;
            AppDialog.Show(
                $"Noted could not close because MiniPad recovery data was not saved.\n\n{message}\n\nThe application will remain open so you can retry.",
                "MiniPad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_scratchpadWindow is not null && !_scratchpadWindow.TryFlushPendingContent(out var error))
        {
            e.Cancel = true;
            CancelRequestedUserBackupRestore();
            var message = error ?? "Scratchpad content could not be saved before shutdown.";
            if (message == _lastShutdownWarning)
                return;

            _lastShutdownWarning = message;
            AppDialog.Show(
                $"Noted could not close because pending Scratchpad content was not saved.\n\n{message}\n\nThe application will remain open so you can retry.",
                "Scratchpad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var toolSaveFailures = _shutdownFlushCoordinator.FlushAll();
        if (toolSaveFailures.Count > 0)
        {
            e.Cancel = true;
            CancelRequestedUserBackupRestore();
            var message = string.Join(
                "\n",
                toolSaveFailures.Select(failure =>
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
        if (_noteEditorWindow is not null && !_noteEditorWindow.TryPrepareForClose())
        {
            e.Cancel = true;
            CancelRequestedUserBackupRestore();
            return;
        }

        if (_restoreUserBackupOnShutdown)
        {
            var restoreResult = _fullBackupService?.ScheduleUserBackupRestore()
                ?? new FullBackupResult(
                    FullBackupStatus.Failed,
                    FullBackupType.User,
                    null,
                    "Backups are not available.");
            if (restoreResult.Status != FullBackupStatus.RestoreScheduled)
            {
                e.Cancel = true;
                _noteEditorWindow?.CancelPreparedClose();
                CancelRequestedUserBackupRestore();
                AppDialog.Show(
                    restoreResult.Message,
                    "Noted - Restore Not Started",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        _checklistWindow?.PrepareForApplicationShutdown();
        _dictionaryWindow?.PrepareForApplicationShutdown();
        _scratchpadWindow?.PrepareForApplicationShutdown();
        _miniPadWindow?.PrepareForApplicationShutdown();
        if (!_restoreUserBackupOnShutdown)
            TryUpdateRecentBackup();
    }

    private void CancelRequestedUserBackupRestore()
    {
        _restoreUserBackupOnShutdown = false;
        try
        {
            _fullBackupService?.CancelPendingUserBackupRestore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }
    }

    // Expected settings failures are recoverable after actionable feedback.
    // Every other unhandled dispatcher exception remains fatal.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is SettingsPersistenceException)
        {
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

    // Catches unhandled exceptions on non-UI threads (e.g. background workers).
    // The runtime may still terminate the process after this fires, but the user
    // sees a clear message rather than a silent crash.
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown error");
        ShowFatalError("A fatal background error occurred.", exception);
    }

    // Catches exceptions from fire-and-forget Tasks that were never awaited.
    // Marking them observed prevents any future runtime behavior from treating
    // them as unhandled and surfacing them to the user.
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
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
        if (Interlocked.Exchange(ref _fatalErrorShown, 1) != 0)
            return;

        var message = exception is SettingsPersistenceException
            ? exception.Message
            : $"{context}\n\n{exception.Message}";

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
            Debug.WriteLine($"The single-instance mutex was not owned by this thread: {ex}");
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
        catch
        {
            // Best-effort; never crash the app over a missing shortcut.
        }
    }
}

