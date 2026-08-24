using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Noted.Services;

namespace Noted;

public partial class MicroScratchpadWindow : OverlayWindow
{
    private readonly IMicroScratchpadRecoveryService _recoveryService;
    private readonly SaveScheduler<string> _saveScheduler;
    private bool _keepDraftOnClose;

    public MicroScratchpadWindow(
        IMicroScratchpadRecoveryService recoveryService,
        MicroScratchpadRecoveryDraft? draft = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryService);

        _recoveryService = recoveryService;

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
        Loaded += MicroScratchpadWindow_Loaded;
        Closing += MicroScratchpadWindow_Closing;
        Closed += MicroScratchpadWindow_Closed;

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

    public bool TryFlushPendingContent(out string? error)
    {
        _saveScheduler.Schedule(Editor.Text);
        return _saveScheduler.TryFlush(out error);
    }

    protected override void SaveWindowState()
    {
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFlushPendingContent(out var error))
        {
            AppDialog.Show(
                error ?? "Micro Scratchpad recovery data could not be saved.",
                "Micro Scratchpad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        HideWindow();
    }

    private void MicroScratchpadWindow_Loaded(object sender, RoutedEventArgs e)
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
            _recoveryService.SaveDraft(content);
            return PersistenceSaveResult.Succeeded();
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return PersistenceSaveResult.Failed(
                $"Micro Scratchpad recovery data could not be saved: {ex.Message}");
        }
    }

    private void SaveScheduler_StateChanged(object? sender, EventArgs e)
    {
        var error = _saveScheduler.LastError;
        PersistenceErrorText.Text = error ?? string.Empty;
        PersistenceErrorPanel.Visibility = string.IsNullOrWhiteSpace(error)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void MicroScratchpadWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_keepDraftOnClose)
            return;

        if (!_saveScheduler.TryFlush(out var error))
        {
            e.Cancel = true;
            AppDialog.Show(
                error ?? "Micro Scratchpad recovery data could not be saved.",
                "Micro Scratchpad Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        e.Cancel = true;
        HideWindow();
    }

    private void MicroScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        _saveScheduler.StateChanged -= SaveScheduler_StateChanged;
        _saveScheduler.Dispose();
        Editor.TextChanged -= Editor_TextChanged;
        Loaded -= MicroScratchpadWindow_Loaded;
        Closing -= MicroScratchpadWindow_Closing;
        Closed -= MicroScratchpadWindow_Closed;
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
