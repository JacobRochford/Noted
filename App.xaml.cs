using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Noted.Services;

namespace Noted;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Noted.SingleInstance";
    private Mutex? _singleInstanceMutex;

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
                MessageBox.Show(
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
            MessageBox.Show(
                "Noted is already running.\n\nCheck your system tray to find the existing instance.",
                "Noted Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        TryCreateDesktopShortcut();
        _ = UpdateService.CheckForUpdatesAsync();

        var settings = new AppSettingsService();
        var fileService = new NoteFileService(settings);
        var mainWindow = new MainWindow(settings, fileService, new NotepadProcessService(), new StartupService());
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    // Catches unhandled exceptions thrown on the UI thread.
    // Setting Handled = true keeps the overlay alive instead of crashing.
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nNoted will continue running. If the problem persists, please restart the app.",
            "Noted — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // Catches unhandled exceptions on non-UI threads (e.g. background workers).
    // The runtime may still terminate the process after this fires, but the user
    // sees a clear message rather than a silent crash.
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = e.ExceptionObject is Exception ex
            ? ex.Message
            : e.ExceptionObject?.ToString() ?? "Unknown error";

        MessageBox.Show(
            $"A fatal error occurred and Noted must close:\n\n{message}",
            "Noted — Fatal Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    // Catches exceptions from fire-and-forget Tasks that were never awaited.
    // Marking them observed prevents any future runtime behavior from treating
    // them as unhandled and surfacing them to the user.
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
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

