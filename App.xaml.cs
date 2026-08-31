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
    private MicroScratchpadWindow? _microScratchpadWindow;
    private IMicroScratchpadRecoveryService? _microScratchpadRecoveryService;
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
            base.OnStartup(e);
            TryCreateDesktopShortcut();

            var settings = new AppSettingsService();
            _settingsService = settings;
            _microScratchpadRecoveryService = new MicroScratchpadRecoveryService(settings.AppDataDirectory);
            var fileService = new NoteFileService(settings);
            startupFileService = fileService;
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
                new StartupService());
            _fileService = fileService;
            _noteEditorWindow = noteEditor;
            _mainWindow = mainWindow;
            MainWindow = mainWindow;
            mainWindow.Closing += MainWindow_Closing;

            WindowManager.Main = mainWindow;
            WindowManager.ChecklistProvider = GetOrCreateChecklistWindow;
            WindowManager.DictionaryProvider = GetOrCreateDictionaryWindow;
            WindowManager.ScratchpadProvider = GetOrCreateScratchpadWindow;
            WindowManager.MicroScratchpadProvider = GetOrCreateMicroScratchpadWindow;
            WindowManager.Editor = noteEditor;

            mainWindow.Show();
            if (settings.LoadChecklistWindowState().ReopenOnStartup)
                WindowManager.ShowChecklistPanel();
            if (settings.LoadDictionaryWindowState().ReopenOnStartup)
                WindowManager.ShowDictionaryPanel();
            if (settings.LoadScratchpadWindowState().ReopenOnStartup)
                WindowManager.ShowScratchpadPanel();

            noteEditor.RestoreEditorSession();
            RestoreMicroScratchpadWindow();
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
            CloseMicroScratchpadWindow();

            _checklistShutdownRegistration?.Dispose();
            _checklistShutdownRegistration = null;
            _dictionaryShutdownRegistration?.Dispose();
            _dictionaryShutdownRegistration = null;

            mainWindow?.CleanupResources();
            try { _fileService?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
            WindowManager.ClearAll();

            _fileService = null;
            _mainWindow = null;
            _noteEditorWindow = null;
            _checklistWindow = null;
            _dictionaryWindow = null;
            _scratchpadWindow = null;
            _microScratchpadWindow = null;
            _microScratchpadRecoveryService = null;
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

    private MicroScratchpadWindow? GetOrCreateMicroScratchpadWindow()
    {
        Dispatcher.VerifyAccess();
        if (_isShuttingDown)
            return _microScratchpadWindow;

        if (_microScratchpadWindow is not null)
            return _microScratchpadWindow;

        var recoveryService = _microScratchpadRecoveryService
            ?? throw new InvalidOperationException("Mini Pad recovery is not initialized.");
        return CreateMicroScratchpadWindow(recoveryService, null);
    }

    private void RestoreMicroScratchpadWindow()
    {
        var recoveryService = _microScratchpadRecoveryService
            ?? throw new InvalidOperationException("Mini Pad recovery is not initialized.");

        var loadResult = recoveryService.LoadDraft();
        if (loadResult.Draft is not null)
        {
            var window = CreateMicroScratchpadWindow(recoveryService, loadResult.Draft);
            window.Show();
        }

        if (loadResult.Issues.Count == 0)
            return;

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
            $"Some Mini Pad recovery files need attention. No recoverable content was silently discarded.\n\n{issueText}{remainingText}",
            "Mini Pad Recovery",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private MicroScratchpadWindow CreateMicroScratchpadWindow(
        IMicroScratchpadRecoveryService recoveryService,
        MicroScratchpadRecoveryDraft? draft)
    {
        var window = new MicroScratchpadWindow(recoveryService, draft);
        window.Closed += MicroScratchpadWindow_Closed;
        _microScratchpadWindow = window;
        WindowManager.MicroScratchpad = window;
        return window;
    }

    private void MicroScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not MicroScratchpadWindow window)
            return;

        window.Closed -= MicroScratchpadWindow_Closed;
        if (!ReferenceEquals(_microScratchpadWindow, window))
            return;

        _microScratchpadWindow = null;
        if (ReferenceEquals(WindowManager.MicroScratchpad, window))
            WindowManager.MicroScratchpad = null;
    }

    private void CloseMicroScratchpadWindow()
    {
        var window = _microScratchpadWindow;
        if (window is null)
            return;

        window.Closed -= MicroScratchpadWindow_Closed;
        if (!window.TryFlushPendingContent(out var error))
        {
            AppDialog.Show(
                error ?? "Mini Pad recovery data could not be saved before shutdown.",
                "Mini Pad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        window.KeepDraftOnClose();
        CloseExistingWindow(window, "Mini Pad");
        _microScratchpadWindow = null;
        if (ReferenceEquals(WindowManager.MicroScratchpad, window))
            WindowManager.MicroScratchpad = null;
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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_microScratchpadWindow is not null &&
            !_microScratchpadWindow.TryFlushPendingContent(out var recoveryError))
        {
            e.Cancel = true;
            var message = recoveryError ?? "Mini Pad recovery data could not be saved.";
            if (message == _lastShutdownWarning)
                return;

            _lastShutdownWarning = message;
            AppDialog.Show(
                $"Noted could not close because Mini Pad recovery data was not saved.\n\n{message}\n\nThe application will remain open so you can retry.",
                "Mini Pad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_scratchpadWindow is not null && !_scratchpadWindow.TryFlushPendingContent(out var error))
        {
            e.Cancel = true;
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
            return;
        }

        _checklistWindow?.PrepareForApplicationShutdown();
        _dictionaryWindow?.PrepareForApplicationShutdown();
        _scratchpadWindow?.PrepareForApplicationShutdown();
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

