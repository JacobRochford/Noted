using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using Noted.Models;
using Noted.ViewModels;

namespace Noted.Services;

internal sealed class ChecklistUrgentReminderAudio : IDisposable
{
    private static ChecklistUrgentReminderAudio? s_channelOwner;
    private readonly ChecklistUrgentRemindersViewModel _model;
    private readonly Func<bool> _canPlay;
    private readonly TimeProvider _clock;
    private readonly Action<ChecklistNotificationOptions>? _testPlay;
    private readonly Action? _testStop;
    private Guid? _playingOccurrence;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, long> _played = [];
    private SoundPlayer? _player;
    private Stream? _stream;
    private bool _disposed;
    internal ChecklistUrgentReminderAudio(ChecklistUrgentRemindersViewModel model, Dispatcher dispatcher,
        Func<bool> canPlay, TimeProvider? clock = null, Action<ChecklistNotificationOptions>? testPlay = null, Action? testStop = null)
    {
        _model = model; _canPlay = canPlay; _clock = clock ?? TimeProvider.System; _testPlay = testPlay; _testStop = testStop;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += Tick;
    }
    internal void Refresh()
    {
        if (_disposed) return;
        if (_model.Entries.Count == 0) { _timer.Stop(); Stop(); return; }
        _timer.Start(); CheckNow();
    }
    internal void CheckNow()
    {
        if (_disposed) return;
        var entries = _model.Entries.Where(row => !row.IsSilenced && row.Options.UrgentSound != ChecklistUrgentSound.Silent).ToArray();
        var active = entries.Select(row => row.OccurrenceId).ToHashSet();
        foreach (var key in _played.Keys.Where(key => !active.Contains(key)).ToArray()) _played.Remove(key);
        if (_playingOccurrence is { } playing && !active.Contains(playing)) Stop();
        if (!_canPlay() || entries.Length == 0) { Stop(); return; }
        // one audio channel for the group; the earliest eligible occurrence supplies the next sound.
        foreach (var entry in entries)
        {
            var options = entry.Options;
            var delay = Math.Max(options.RepeatSeconds, _clipSeconds);
            if (_played.TryGetValue(entry.OccurrenceId, out var last) &&
                (!options.RepeatSound || _clock.GetElapsedTime(last).TotalSeconds < delay)) continue;
            if (_lastPlayed.HasValue && _clock.GetElapsedTime(_lastPlayed.Value).TotalSeconds < _clipSeconds) return;
            if (!Play(options)) return;
            _playingOccurrence = entry.OccurrenceId;
            foreach (var audible in entries) _played[audible.OccurrenceId] = _clock.GetTimestamp();
            _lastPlayed = _clock.GetTimestamp();
            return;
        }
    }
    private double _clipSeconds = 1;
    private long? _lastPlayed;
    private bool Play(ChecklistNotificationOptions options)
    {
        // SoundPlayer is process-wide; a preview must never replace or stop a real urgent alarm.
        if (_model.IsPreview && s_channelOwner is { } owner && !owner._model.IsPreview)
        {
            Stop();
            _model.ShowMessage("Preview sound is paused while a real urgent alarm is active. Silence that alarm to hear the preview.");
            return false;
        }
        s_channelOwner?.Stop();
        Stop();
        s_channelOwner = this;
        if (_testPlay is not null) { _testPlay(options); return true; }
        try
        {
            if (options.UrgentSound == ChecklistUrgentSound.Custom)
            {
                _clipSeconds = ValidateWav(options.CustomSoundPath!);
                _player = new SoundPlayer(options.CustomSoundPath!);
            }
            else LoadBuiltIn();
            _player!.Load(); _player.Play();
            return true;
        }
        catch (Exception exception)
        {
            ExceptionDiagnostics.Record(exception, "Play urgent reminder sound");
            _model.ShowMessage("The selected sound is unavailable. Using the built-in alarm.");
            Stop();
            s_channelOwner = this;
            try { LoadBuiltIn(); _player!.Load(); _player.Play(); return true; }
            catch (Exception fallback)
            {
                Stop();
                ExceptionDiagnostics.Record(fallback, "Play built-in reminder sound");
                _model.ShowMessage("Sound could not be played. This reminder still needs acknowledgment.");
                return false;
            }
        }
    }
    private void LoadBuiltIn()
    {
        _clipSeconds = 1;
        _stream = Application.GetResourceStream(new Uri("/Noted;component/Resources/ReminderAlarm.wav", UriKind.Relative))!.Stream;
        _player = new SoundPlayer(_stream);
    }
    internal static double ValidateWav(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new IOException("Choose an existing WAV file.");
        FileSystemLinkValidator.ValidatePath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 2 * 1024 * 1024) throw new IOException("Choose a WAV file smaller than 2 MB.");
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new IOException("Choose an uncompressed PCM WAV file.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new IOException("Choose a WAV file.");
        uint bytesPerSecond = 0, dataLength = 0;
        ushort blockAlign = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4)); var length = reader.ReadUInt32(); var next = stream.Position + length + (length % 2);
            if (next > stream.Length) throw new IOException("The WAV file is incomplete.");
            if (id == "fmt " && length >= 16)
            {
                if (reader.ReadUInt16() != 1) throw new IOException("Choose an uncompressed PCM WAV file.");
                var channels = reader.ReadUInt16(); var rate = reader.ReadUInt32(); bytesPerSecond = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                if (channels is < 1 or > 2 || rate is < 8000 or > 96000 || bits != 16) throw new IOException("Use a mono or stereo 16-bit PCM WAV file.");
                if (blockAlign != channels * 2 || bytesPerSecond != rate * blockAlign)
                    throw new IOException("The WAV timing information is inconsistent.");
            }
            if (id == "data") dataLength = length;
            stream.Position = next;
        }
        var seconds = bytesPerSecond > 0 ? (double)dataLength / bytesPerSecond : 0;
        if (blockAlign == 0 || dataLength % blockAlign != 0 || seconds <= 0 || seconds > 30) throw new IOException("Choose a WAV sound between 0 and 30 seconds.");
        return seconds;
    }
    private void Tick(object? sender, EventArgs e) => CheckNow();
    internal void Stop()
    {
        _playingOccurrence = null; _lastPlayed = null; _clipSeconds = 1;
        if (ReferenceEquals(s_channelOwner, this))
        {
            s_channelOwner = null;
            _testStop?.Invoke();
            _player?.Stop();
        }
        _player?.Dispose(); _player = null; _stream?.Dispose(); _stream = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _timer.Tick -= Tick; Stop(); }
}
