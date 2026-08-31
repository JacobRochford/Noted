using System.Diagnostics;
using System.Windows.Threading;

namespace Noted.Services;

internal readonly record struct PersistenceSaveResult(
    bool Success,
    string? Error,
    string? Warning)
{
    internal static PersistenceSaveResult Succeeded(string? warning = null) =>
        new(true, null, warning);

    internal static PersistenceSaveResult Failed(string error, string? warning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new PersistenceSaveResult(false, error, warning);
    }
}

internal sealed class SaveScheduler<TSnapshot> : IDisposable
{
    private static readonly TimeSpan MinimumTimerInterval = TimeSpan.FromMilliseconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maximumDelay;
    private readonly Func<TSnapshot, PersistenceSaveResult> _saveSnapshot;
    private TSnapshot? _pendingSnapshot;
    private bool _hasPendingSnapshot;
    private long _firstPendingTimestamp;
    private long _lastChangeTimestamp;
    private long _nextRevision;
    private long _pendingRevision;
    private bool _disposed;

    internal SaveScheduler(
        Dispatcher dispatcher,
        TimeSpan quietPeriod,
        TimeSpan maximumDelay,
        Func<TSnapshot, PersistenceSaveResult> saveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(saveSnapshot);
        if (quietPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod), "The quiet period cannot be negative.");
        if (maximumDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumDelay), "The maximum delay must be positive.");

        _dispatcher = dispatcher;
        _quietPeriod = quietPeriod;
        _maximumDelay = maximumDelay;
        _saveSnapshot = saveSnapshot;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Timer_Tick;
    }

    internal bool HasPendingSave => _hasPendingSnapshot;

    internal string? LastError { get; private set; }

    internal string? LastWarning { get; private set; }

    internal long LastSavedRevision { get; private set; }

    internal event EventHandler? StateChanged;

    internal void Schedule(TSnapshot snapshot)
    {
        VerifyUsable();
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = Stopwatch.GetTimestamp();
        if (!_hasPendingSnapshot)
            _firstPendingTimestamp = now;

        _hasPendingSnapshot = true;
        _pendingSnapshot = snapshot;
        _lastChangeTimestamp = now;
        _pendingRevision = checked(++_nextRevision);
        ScheduleTimer(now);
        OnStateChanged();
    }

    internal bool TryFlush(out string? error)
    {
        VerifyUsable();
        _timer.Stop();

        if (!_hasPendingSnapshot)
        {
            error = null;
            return true;
        }

        var snapshot = _pendingSnapshot!;
        var revision = _pendingRevision;
        var result = _saveSnapshot(snapshot);
        if (result.Success)
        {
            LastSavedRevision = revision;
            LastError = null;
            LastWarning = result.Warning;
            if (_pendingRevision == revision)
            {
                _pendingSnapshot = default;
                _hasPendingSnapshot = false;
            }

            error = null;
            OnStateChanged();
            return true;
        }

        LastError = string.IsNullOrWhiteSpace(result.Error)
            ? "The pending data could not be saved."
            : result.Error;
        LastWarning = result.Warning;
        error = LastError;

        if (_pendingRevision == revision)
        {
            var now = Stopwatch.GetTimestamp();
            _firstPendingTimestamp = now;
            _lastChangeTimestamp = now;
            ScheduleTimer(now);
        }

        OnStateChanged();
        return false;
    }

    internal void CancelPending()
    {
        VerifyUsable();
        _timer.Stop();
        _pendingSnapshot = default;
        _hasPendingSnapshot = false;
        LastError = null;
        LastWarning = null;
        OnStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _dispatcher.VerifyAccess();
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _pendingSnapshot = default;
        _hasPendingSnapshot = false;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        TryFlush(out _);
    }

    private void ScheduleTimer(long now)
    {
        var quietRemaining = _quietPeriod -
            Stopwatch.GetElapsedTime(_lastChangeTimestamp, now);
        var maximumRemaining = _maximumDelay -
            Stopwatch.GetElapsedTime(_firstPendingTimestamp, now);
        var interval = quietRemaining <= maximumRemaining
            ? quietRemaining
            : maximumRemaining;

        _timer.Stop();
        _timer.Interval = interval > TimeSpan.Zero
            ? interval
            : MinimumTimerInterval;
        _timer.Start();
    }

    private void VerifyUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
