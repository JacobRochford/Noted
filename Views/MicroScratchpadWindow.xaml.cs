using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Services;

namespace Noted;

public partial class MicroScratchpadWindow : Window
{
    private readonly IMicroScratchpadRecoveryService _recoveryService;
    private readonly Guid _draftId;
    private readonly SaveScheduler<string> _saveScheduler;
    private bool _keepDraftOnClose;

    public MicroScratchpadWindow(
        IMicroScratchpadRecoveryService recoveryService,
        MicroScratchpadRecoveryDraft? draft = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryService);

        _recoveryService = recoveryService;
        _draftId = draft?.Id ?? Guid.NewGuid();

        InitializeComponent();
        Title = $"Micro Scratchpad [{GetShortId(_draftId)}]";
        Editor.Text = draft?.Content ?? string.Empty;
        _saveScheduler = new SaveScheduler<string>(
            Dispatcher,
            quietPeriod: TimeSpan.FromMilliseconds(750),
            maximumDelay: TimeSpan.FromSeconds(2),
            SaveDraftSnapshot);
        _saveScheduler.StateChanged += SaveScheduler_StateChanged;
        Editor.TextChanged += Editor_TextChanged;
        SourceInitialized += MicroScratchpadWindow_SourceInitialized;
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

    private void MicroScratchpadWindow_SourceInitialized(object? sender, EventArgs e)
    {
        WindowInterop.RemoveMinimizeAndMaximizeBoxes(new WindowInteropHelper(this).Handle);
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
            _recoveryService.SaveDraft(_draftId, content);
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

        _saveScheduler.CancelPending();
        try
        {
            _recoveryService.DeleteDraft(_draftId);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            e.Cancel = true;
            _saveScheduler.Schedule(Editor.Text);
            var contentWasSaved = _saveScheduler.TryFlush(out var saveError);
            var saveFailureDetail = contentWasSaved
                ? string.Empty
                : $"\n\nThe current text also could not be recovery-saved:\n{saveError}";
            AppDialog.Show(
                $"The Micro Scratchpad could not be closed because its recovery data could not be removed.\n\n{ex.Message}{saveFailureDetail}",
                "Micro Scratchpad Close Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MicroScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        _saveScheduler.StateChanged -= SaveScheduler_StateChanged;
        _saveScheduler.Dispose();
        Editor.TextChanged -= Editor_TextChanged;
        SourceInitialized -= MicroScratchpadWindow_SourceInitialized;
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

    private static string GetShortId(Guid id) =>
        id.ToString("N")[..8].ToUpperInvariant();
}
