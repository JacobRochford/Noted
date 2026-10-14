using System.IO;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

internal sealed class JotCaptureViewModel : ObservableObject, IDisposable
{
    private readonly JotsWorkspace _workspace;
    private readonly JotCaptureDraftService _draftService;
    private readonly SaveScheduler<JotCaptureDraft> _draftScheduler;
    private JotCaptureDraft _draft;
    private bool _isSaving;
    private bool _savedAwaitingClear;
    private string? _error;

    internal JotCaptureViewModel(JotsWorkspace workspace, JotCaptureDraftService draftService, Dispatcher dispatcher)
    {
        _workspace = workspace;
        _draftService = draftService;
        _draft = draftService.Load(out _error);
        RecoveryIssuesFoundThisRun = !string.IsNullOrWhiteSpace(_error);
        _draftScheduler = new SaveScheduler<JotCaptureDraft>(dispatcher,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2), SaveDraft);
    }

    public string Text
    {
        get => _draft.Text;
        set
        {
            if (_draft.Text == value || _savedAwaitingClear)
                return;
            _draft = new JotCaptureDraft { Text = value ?? "" };
            _draftScheduler.Schedule(_draft);
            OnPropertyChanged();
        }
    }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool IsReadOnly => _savedAwaitingClear;
    internal bool RecoveryIssuesFoundThisRun { get; }

    internal bool TrySave()
    {
        if (_isSaving || string.IsNullOrWhiteSpace(Text))
            return false;

        _isSaving = true;
        try
        {
            // save the draft ID first so retrying after a restart won't duplicate the jot.
            _draftScheduler.Schedule(_draft);
            if (!_draftScheduler.TryFlush(out var error))
                return false;
            if (!_workspace.TryCapture(_draft.Id, Text, out error))
            {
                Error = error;
                return false;
            }

            _savedAwaitingClear = true;
            OnPropertyChanged(nameof(IsReadOnly));
            var cleared = new JotCaptureDraft();
            var result = SaveDraft(cleared);
            if (!result.Success)
            {
                Error = "The jot was saved, but its draft could not be cleared. Press Enter to retry; no duplicate will be added.";
                return false;
            }
            _draftScheduler.CancelPending();
            _draft = cleared;
            _savedAwaitingClear = false;
            Error = null;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsReadOnly));
            return true;
        }
        finally { _isSaving = false; }
    }

    internal bool TryFlushDraft(out string? error) => _draftScheduler.TryFlush(out error);

    private PersistenceSaveResult SaveDraft(JotCaptureDraft draft)
    {
        try
        {
            Error = _draftService.Save(draft);
            return PersistenceSaveResult.Succeeded(Error);
        }
        catch (Exception ex) when (FileSystemErrors.IsExpected(ex))
        {
            ExceptionDiagnostics.Record(ex);
            Error = "The capture draft could not be saved. Your text is still available here; try again.";
            return PersistenceSaveResult.Failed(Error);
        }
    }

    public void Dispose() => _draftScheduler.Dispose();
}
