namespace Noted.Services;

internal sealed record ShutdownFlushFailure(string ParticipantName, string Error);

internal sealed class ShutdownFlushCoordinator
{
    private readonly List<Registration> _registrations = [];

    internal IDisposable Register(
        string participantName,
        Func<PersistenceSaveResult> flush)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantName);
        ArgumentNullException.ThrowIfNull(flush);

        var registration = new Registration(this, participantName, flush);
        _registrations.Add(registration);
        return registration;
    }

    internal IReadOnlyList<ShutdownFlushFailure> FlushAll()
    {
        var failures = new List<ShutdownFlushFailure>();
        foreach (var registration in _registrations.ToArray())
        {
            if (!registration.IsActive)
                continue;

            var result = registration.Flush();
            if (!result.Success)
            {
                failures.Add(new ShutdownFlushFailure(
                    registration.ParticipantName,
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "Pending data could not be saved."
                        : result.Error));
            }
        }

        return failures;
    }

    private void Unregister(Registration registration)
    {
        registration.Deactivate();
        _registrations.Remove(registration);
    }

    private sealed class Registration : IDisposable
    {
        private readonly ShutdownFlushCoordinator _owner;
        private Func<PersistenceSaveResult>? _flush;

        internal Registration(
            ShutdownFlushCoordinator owner,
            string participantName,
            Func<PersistenceSaveResult> flush)
        {
            _owner = owner;
            ParticipantName = participantName;
            _flush = flush;
        }

        internal string ParticipantName { get; }

        internal bool IsActive => _flush is not null;

        internal PersistenceSaveResult Flush()
        {
            return _flush?.Invoke() ?? PersistenceSaveResult.Succeeded();
        }

        internal void Deactivate() => _flush = null;

        public void Dispose()
        {
            if (!IsActive)
                return;

            _owner.Unregister(this);
        }
    }
}
