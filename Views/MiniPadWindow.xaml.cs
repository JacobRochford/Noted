using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted;

public partial class MiniPadWindow : OverlayWindow
{
    private readonly IMiniPadRecoveryService _recoveryService;
    private readonly IAppSettingsService _settingsService;
    private readonly SaveScheduler<string> _saveScheduler;
    private bool _keepDraftOnClose;
    private bool _preserveOpenStateOnClose;
    private bool _reopenOnStartup;

    public MiniPadWindow(
        IMiniPadRecoveryService recoveryService,
        IAppSettingsService settingsService,
        MiniPadRecoveryDraft? draft = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryService);
        ArgumentNullException.ThrowIfNull(settingsService);

        _recoveryService = recoveryService;
        _settingsService = settingsService;
        _reopenOnStartup = settingsService.LoadMiniPadWindowState().ReopenOnStartup;

        InitializeComponent();
        InitializeOverlay(
            ghostModeEnabled: false,
            ghostModeOpacity: 1,
            defaultOpacity: 1);
        Editor.Text = draft?.Content ?? string.Empty;
        _saveScheduler = new SaveScheduler<string>(
            Dispatcher,
            quietPeriod: TimeSpan.FromMilliseconds(750),
            maximumDelay: TimeSpan.FromSeconds(2),
            SaveDraftSnapshot);
        _saveScheduler.StateChanged += SaveScheduler_StateChanged;
        Editor.TextChanged += Editor_TextChanged;
        Loaded += MiniPadWindow_Loaded;
        IsVisibleChanged += MiniPadWindow_IsVisibleChanged;
        Closing += MiniPadWindow_Closing;
        Closed += MiniPadWindow_Closed;

        if (draft is null)
        {
            _saveScheduler.Schedule(Editor.Text);
            _saveScheduler.TryFlush(out _);
        }
    }

    public void KeepDraftOnClose()
    {
        _keepDraftOnClose = true;
    }

    internal void PrepareForApplicationShutdown()
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        _preserveOpenStateOnClose = true;
        SaveWindowState();
    }

    public bool TryFlushPendingContent(out string? error)
    {
        _saveScheduler.Schedule(Editor.Text);
        return _saveScheduler.TryFlush(out error);
    }

    internal string? BackupBlockingIssue =>
        _saveScheduler.LastError ?? _saveScheduler.LastWarning;

    protected override void SaveWindowState()
    {
        _settingsService.SaveMiniPadWindowState(new MiniPadWindowState
        {
            ReopenOnStartup = _reopenOnStartup
        });
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFlushPendingContent(out var error))
        {
            AppDialog.Show(
                error ?? "MiniPad recovery data could not be saved.",
                "MiniPad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _reopenOnStartup = false;
        SaveWindowState();
        HideWindow();
    }

    private void MiniPadWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_preserveOpenStateOnClose || IsChangingGroupVisibility)
            return;

        _reopenOnStartup = IsWindowVisible;
        SaveWindowState();
    }

    private void MiniPadWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, Editor.Focus);
    }

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _saveScheduler.Schedule(Editor.Text);
    }

    private PersistenceSaveResult SaveDraftSnapshot(string content)
    {
        try
        {
            var warning = _recoveryService.SaveDraft(content);
            return PersistenceSaveResult.Succeeded(warning);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return PersistenceSaveResult.Failed(
                $"MiniPad recovery data could not be saved: {ex.Message}");
        }
    }

    private void SaveScheduler_StateChanged(object? sender, EventArgs e)
    {
        var message = _saveScheduler.LastError ?? _saveScheduler.LastWarning;
        PersistenceErrorText.Text = message ?? string.Empty;
        PersistenceErrorPanel.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void MiniPadWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_keepDraftOnClose)
            return;

        if (!_saveScheduler.TryFlush(out var error))
        {
            e.Cancel = true;
            AppDialog.Show(
                error ?? "MiniPad recovery data could not be saved.",
                "MiniPad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_preserveOpenStateOnClose)
        {
            _reopenOnStartup = false;
            SaveWindowState();
        }

        e.Cancel = true;
        HideWindow();
    }

    private void MiniPadWindow_Closed(object? sender, EventArgs e)
    {
        _saveScheduler.StateChanged -= SaveScheduler_StateChanged;
        _saveScheduler.Dispose();
        Editor.TextChanged -= Editor_TextChanged;
        Loaded -= MiniPadWindow_Loaded;
        IsVisibleChanged -= MiniPadWindow_IsVisibleChanged;
        Closing -= MiniPadWindow_Closing;
        Closed -= MiniPadWindow_Closed;
    }

    private static bool IsExpectedRecoveryException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            ArgumentException or
            NotSupportedException;
    }

}
