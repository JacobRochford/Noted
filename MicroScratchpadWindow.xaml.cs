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
    private readonly DispatcherTimer _saveTimer;
    private bool _keepDraftOnClose;

    public MicroScratchpadWindow(
        IMicroScratchpadRecoveryService recoveryService,
        MicroScratchpadRecoveryDraft? draft = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryService);

        _recoveryService = recoveryService;
        _draftId = draft?.Id ?? Guid.NewGuid();
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _saveTimer.Tick += SaveTimer_Tick;

        InitializeComponent();
        Editor.Text = draft?.Content ?? string.Empty;
        Editor.TextChanged += Editor_TextChanged;
        SourceInitialized += MicroScratchpadWindow_SourceInitialized;
        Loaded += MicroScratchpadWindow_Loaded;
        Closing += MicroScratchpadWindow_Closing;
        Closed += MicroScratchpadWindow_Closed;

        if (draft is null)
            TrySaveDraft(out _);
    }

    public void KeepDraftOnClose()
    {
        _keepDraftOnClose = true;
    }

    public bool TryFlushPendingContent(out string? error)
    {
        _saveTimer.Stop();
        return TrySaveDraft(out error);
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
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        TrySaveDraft(out _);
    }

    private bool TrySaveDraft(out string? error)
    {
        try
        {
            _recoveryService.SaveDraft(_draftId, Editor.Text);
            error = null;
            return true;
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            error = $"Micro Scratchpad recovery data could not be saved: {ex.Message}";
            return false;
        }
    }

    private void MicroScratchpadWindow_Closing(object? sender, CancelEventArgs e)
    {
        _saveTimer.Stop();
        if (_keepDraftOnClose)
            return;

        try
        {
            _recoveryService.DeleteDraft(_draftId);
        }
        catch (Exception ex) when (IsExpectedRecoveryException(ex))
        {
            System.Diagnostics.Debug.WriteLine(ex);
            e.Cancel = true;
            AppDialog.Show(
                $"The Micro Scratchpad could not be closed because its recovery data could not be removed.\n\n{ex.Message}",
                "Micro Scratchpad Close Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MicroScratchpadWindow_Closed(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
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
}
