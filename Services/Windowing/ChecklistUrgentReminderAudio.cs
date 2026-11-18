using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using Noted.Models;
using Noted.ViewModels;

namespace Noted.Services;

internal sealed class ChecklistUrgentReminderAudio : IDisposable
{
    internal static readonly TimeSpan ChimeDuration = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan s_repeatInterval = ChimeDuration + TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_retryInterval = TimeSpan.FromSeconds(5);
    private static ChecklistUrgentReminderAudio? s_channelOwner;

    private readonly ChecklistUrgentRemindersViewModel _model;
    private readonly Func<bool> _canPlay;
    private readonly TimeProvider _clock;
    private readonly Action? _testPlay;
    private readonly Action? _testStop;
    private readonly bool _soundOnly;
    private readonly DispatcherTimer _timer;
    private Guid? _playingOccurrence;
    private long? _lastPlayed;
    private bool _playedSoundOnly;
    private SoundPlayer? _player;
    private Stream? _stream;
    private bool _disposed;

    internal ChecklistUrgentReminderAudio(ChecklistUrgentRemindersViewModel model, Dispatcher dispatcher,
        Func<bool> canPlay, TimeProvider? clock = null, Action? testPlay = null, Action? testStop = null, bool soundOnly = false)
    {
        _model = model;
        _canPlay = canPlay;
        _clock = clock ?? TimeProvider.System;
        _testPlay = testPlay;
        _testStop = testStop;
        _soundOnly = soundOnly;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Tick;
    }

    internal void Refresh() => CheckNow();

    internal void CheckNow()
    {
        if (_disposed) return;
        _timer.Stop();
        var source = _model.Entries.FirstOrDefault(IsAudible);
        if (source is null || !_canPlay())
        {
            Stop();
            // recheck when visibility or input desktop access changes without a presenter event.
            if (source is not null && !_playedSoundOnly) Schedule(s_repeatInterval);
            return;
        }

        if (_playingOccurrence is { } playing && !_model.Entries.Any(row => row.OccurrenceId == playing && IsAudible(row)))
            StopClip();
        if (_playedSoundOnly) return;

        // one cadence for the group, even when individual reminders arrive or are silenced.
        if (_lastPlayed is { } last)
        {
            var remaining = s_repeatInterval - _clock.GetElapsedTime(last);
            if (remaining > TimeSpan.Zero)
            {
                Schedule(remaining);
                return;
            }
        }

        if (_model.IsPreview && s_channelOwner is { } owner && !owner._model.IsPreview)
        {
            _model.ShowMessage("Preview sound is paused while a real urgent alarm is active. Silence that alarm to hear the preview.");
            if (!_soundOnly) Schedule(s_repeatInterval);
            return;
        }

        if (!ReferenceEquals(s_channelOwner, this))
        {
            s_channelOwner?.ReleaseChannel();
            s_channelOwner = this;
        }

        try
        {
            PlayChime();
        }
        catch (Exception exception)
        {
            ReleaseChannel();
            ExceptionDiagnostics.Record(exception, "Play urgent reminder chime");
            _model.ShowMessage("The alarm sound could not be played. This reminder still needs acknowledgment.");
            if (!_soundOnly) Schedule(s_retryInterval);
            return;
        }

        _playingOccurrence = source.OccurrenceId;
        _lastPlayed = _clock.GetTimestamp();
        if (_soundOnly) _playedSoundOnly = true;
        else Schedule(s_repeatInterval);
    }

    private static bool IsAudible(ChecklistUrgentReminderEntry row) =>
        !row.IsSilenced && row.Options.UrgentSound == ChecklistUrgentSound.Chime;

    private void PlayChime()
    {
        if (_testPlay is not null)
        {
            _testPlay();
            return;
        }

        if (_player is null)
        {
            _stream = Application.GetResourceStream(new Uri("/Noted;component/Resources/ReminderChime.wav", UriKind.Relative))!.Stream;
            _player = new SoundPlayer(_stream);
            _player.Load();
        }
        _player.Play();
    }

    private void Schedule(TimeSpan delay)
    {
        _timer.Interval = delay;
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs e) => CheckNow();

    private void StopClip()
    {
        _playingOccurrence = null;
        if (!ReferenceEquals(s_channelOwner, this)) return;
        _testStop?.Invoke();
        _player?.Stop();
    }

    private void ReleaseChannel()
    {
        StopClip();
        if (ReferenceEquals(s_channelOwner, this)) s_channelOwner = null;
        _player?.Dispose();
        _player = null;
        _stream?.Dispose();
        _stream = null;
    }

    internal void Stop()
    {
        _timer.Stop();
        _lastPlayed = null;
        ReleaseChannel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Tick -= Tick;
        Stop();
    }
}
