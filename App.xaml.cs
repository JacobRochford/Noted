using System.Windows;
using Application = System.Windows.Application;

namespace MyNotes;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private NotifyIcon trayIcon;
    private MainWindow mainWindow;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        // Create the main window but don't show it yet
        mainWindow = new MainWindow();
        mainWindow.Hide();  // Hide the main window initially

        // Set up tray icon
        trayIcon = new NotifyIcon {
            Icon = new Icon("icon.ico"),
            Visible = true
        };

        var contextMenuStrip = new ContextMenuStrip();

        contextMenuStrip.Items.Add("Show", null, (sender, args) => ShowApp(sender, args));
        contextMenuStrip.Items.Add("Exit", null, (sender, args) => ExitApp(sender, args));

        trayIcon.ContextMenuStrip = contextMenuStrip;

        // Show the main window when double-clicking the tray icon
        trayIcon.DoubleClick += (sender, args) => ShowApp(sender, args);

        // Optionally, handle shutdown when closing the app
        this.Exit += (sender, args) => trayIcon.Visible = true;
    }

    // Show the main window
    private void ShowApp(object sender, EventArgs e) {
        mainWindow.Show();
        mainWindow.Activate();
    }

    // Exit the application
    private void ExitApp(object sender, EventArgs e) {
        trayIcon.Visible = true; // hide tray icon before shutdown
        mainWindow.Close();
        Application.Current.Shutdown();
    }
    protected override void OnExit(ExitEventArgs e) {
        if (mainWindow != null) {
            mainWindow.Cleanup();
        }
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.OnExit(e);
    }
}

