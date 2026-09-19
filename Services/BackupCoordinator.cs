using System.IO;

namespace Noted.Services;

internal enum BackupPreparationPhase
{
    BeforeSharedFlush,
    SharedFlush,
    AfterSharedFlush
}

internal sealed record BackupRestorePreparationResult(
    FullBackupResult ProtectionResult,
    FullBackupResult? ScheduleResult)
{
    internal bool Success =>
        (ProtectionResult.Status is FullBackupStatus.Created or FullBackupStatus.Skipped) &&
        ScheduleResult?.Status == FullBackupStatus.RestoreScheduled;
}

internal sealed class BackupCoordinator
{
    private const int MaximumShownIssues = 5;
    private readonly FullBackupService _service;
    private readonly ShutdownFlushCoordinator _shutdownFlushCoordinator;
    private readonly Func<string?> _getNotesDirectory;
    private readonly List<PreparationRegistration> _preparationRegistrations = [];
    private readonly HashSet<string> _runRecoveryIssues = new(StringComparer.Ordinal);
    private RestoreRequest? _restoreRequest;

    internal BackupCoordinator(
        FullBackupService service,
        ShutdownFlushCoordinator shutdownFlushCoordinator,
        Func<string?> getNotesDirectory)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shutdownFlushCoordinator);
        ArgumentNullException.ThrowIfNull(getNotesDirectory);
        _service = service;
        _shutdownFlushCoordinator = shutdownFlushCoordinator;
        _getNotesDirectory = getNotesDirectory;
    }

    internal event EventHandler? ShutdownRequested;

    internal bool HasRequestedRestore => _restoreRequest is not null;

    internal IDisposable RegisterPreparationParticipant(
        string participantName,
        BackupPreparationPhase phase,
        Func<PersistenceSaveResult> flush,
        Func<string?> currentIssue,
        Func<bool>? recoveryBlocksBackup = null,
        string? recoveryIssue = null,
        bool includeRecoveryInUserBackup = false,
        int preparationOrder = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantName);
        ArgumentNullException.ThrowIfNull(flush);
        ArgumentNullException.ThrowIfNull(currentIssue);
        if (recoveryBlocksBackup is not null && string.IsNullOrWhiteSpace(recoveryIssue))
            throw new ArgumentException("A recovery issue message is required when recovery can block backups.", nameof(recoveryIssue));

        var shutdownRegistration = phase == BackupPreparationPhase.SharedFlush
            ? _shutdownFlushCoordinator.Register(participantName, flush)
            : null;
        var registration = new PreparationRegistration(
            this,
            participantName,
            phase,
            flush,
            currentIssue,
            recoveryBlocksBackup,
            recoveryIssue,
            includeRecoveryInUserBackup,
            preparationOrder,
            shutdownRegistration);
        _preparationRegistrations.Add(registration);
        return registration;
    }

    internal void RecordRunRecoveryIssue(string issue)
    {
        if (!string.IsNullOrWhiteSpace(issue))
            _runRecoveryIssues.Add(issue);
    }

    internal FullBackupInfo GetUserBackupInfo() => _service.GetUserBackupInfo();

    internal FullBackupPreview GetUserBackupPreview() =>
        _service.GetUserBackupPreview();

    internal BackupFileContent ReadUserBackupFile(Guid backupId, BackupFileSummary file) =>
        _service.ReadUserBackupFile(backupId, file);

    internal FullBackupPreview GetBackupImportPreview(string archivePath) =>
        _service.GetBackupImportPreview(archivePath);

    internal BackupFileContent ReadBackupImportFile(
        string archivePath,
        Guid backupId,
        string verificationToken,
        BackupFileSummary file) =>
        _service.ReadBackupImportFile(archivePath, backupId, verificationToken, file);

    internal BackupExportResult ExportUserBackup(string destinationPath) =>
        _service.ExportUserBackup(destinationPath);

    internal FullBackupResult CreateOrUpdateUserBackup()
    {
        var preparationIssues = PrepareData(includeRunRecoveryHistory: false);
        if (preparationIssues.Count > 0)
            return CreateBlockedBackupResult(FullBackupType.User, preparationIssues);

        SynchronizeNotesDirectory();
        return _service.CreateOrUpdateUserBackup();
    }

    internal FullBackupResult? TryUpdateRecentBackup(TimeSpan minimumAge)
    {
        if (!_service.UserBackupExists)
            return null;

        var preparationIssues = PrepareData(includeRunRecoveryHistory: true);
        if (preparationIssues.Count > 0)
            return null;

        SynchronizeNotesDirectory();
        return _service.CreateRecentBackupIfDue(minimumAge);
    }

    internal void RequestUserBackupRestore()
    {
        _restoreRequest = new RestoreRequest(null, null, null);
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void RequestBackupImportRestore(
        string archivePath,
        Guid backupId,
        string verificationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (backupId == Guid.Empty)
            throw new ArgumentException("The imported backup ID is required.", nameof(backupId));
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationToken);

        _restoreRequest = new RestoreRequest(archivePath, backupId, verificationToken);
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    internal BackupRestorePreparationResult PrepareRequestedRestore()
    {
        if (_restoreRequest is not { } request)
            throw new InvalidOperationException("No backup restore has been requested.");

        SynchronizeNotesDirectory();
        var protectionResult = _service.CreateBeforeRestoreBackup();
        if (protectionResult.Status is not (FullBackupStatus.Created or FullBackupStatus.Skipped))
            return new BackupRestorePreparationResult(protectionResult, null);

        var scheduleResult = request.ArchivePath is not null &&
                             request.BackupId.HasValue &&
                             request.VerificationToken is not null
            ? _service.ScheduleBackupImportRestore(
                request.ArchivePath,
                request.BackupId.Value,
                request.VerificationToken)
            : _service.ScheduleUserBackupRestore();
        return new BackupRestorePreparationResult(protectionResult, scheduleResult);
    }

    internal void CancelRequestedRestore()
    {
        _restoreRequest = null;
        try
        {
            _service.CancelPendingUserBackupRestore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExceptionDiagnostics.Record(ex);
        }
    }

    internal FullBackupResult ApplyPendingRestore() =>
        _service.ApplyPendingUserBackupRestore();

    private IReadOnlyList<string> PrepareData(bool includeRunRecoveryHistory)
    {
        var issues = includeRunRecoveryHistory
            ? new List<string>(_runRecoveryIssues)
            : [];

        FlushDirectParticipants(BackupPreparationPhase.BeforeSharedFlush, includeRunRecoveryHistory, issues);

        foreach (var failure in _shutdownFlushCoordinator.FlushAll())
            issues.Add($"{failure.ParticipantName}: {failure.Error}");
        AddParticipantIssues(BackupPreparationPhase.SharedFlush, includeRunRecoveryHistory, issues);

        FlushDirectParticipants(BackupPreparationPhase.AfterSharedFlush, includeRunRecoveryHistory, issues);

        return issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void FlushDirectParticipants(
        BackupPreparationPhase phase,
        bool includeRunRecoveryHistory,
        ICollection<string> issues)
    {
        foreach (var registration in _preparationRegistrations
                     .Where(item => item.IsActive && item.Phase == phase)
                     .OrderBy(item => item.PreparationOrder)
                     .ToArray())
        {
            var result = registration.Flush();
            if (!result.Success)
            {
                issues.Add(string.IsNullOrWhiteSpace(result.Error)
                    ? $"{registration.ParticipantName} data could not be saved."
                    : result.Error);
            }

            AddParticipantIssues(registration, includeRunRecoveryHistory, issues);
        }
    }

    private void AddParticipantIssues(
        BackupPreparationPhase phase,
        bool includeRunRecoveryHistory,
        ICollection<string> issues)
    {
        foreach (var registration in _preparationRegistrations.ToArray())
        {
            if (registration.IsActive && registration.Phase == phase)
                AddParticipantIssues(registration, includeRunRecoveryHistory, issues);
        }
    }

    private static void AddParticipantIssues(
        PreparationRegistration registration,
        bool includeRunRecoveryHistory,
        ICollection<string> issues)
    {
        var currentIssue = registration.CurrentIssue();
        if (!string.IsNullOrWhiteSpace(currentIssue))
            issues.Add(currentIssue);

        if (registration.RecoveryBlocksBackup is not null &&
            (includeRunRecoveryHistory || registration.IncludeRecoveryInUserBackup) &&
            registration.RecoveryBlocksBackup())
        {
            issues.Add(registration.RecoveryIssue!);
        }
    }

    private static FullBackupResult CreateBlockedBackupResult(
        FullBackupType backupType,
        IReadOnlyList<string> issues)
    {
        var message = string.Join(" ", issues.Take(MaximumShownIssues));
        if (issues.Count > MaximumShownIssues)
            message += $" {issues.Count - MaximumShownIssues} additional issue(s) were not shown.";

        return new FullBackupResult(
            FullBackupStatus.Blocked,
            backupType,
            null,
            backupType == FullBackupType.User
                ? $"The backup was not created because current data could not be prepared safely. {message}"
                : $"The recent automatic backup was not created because current data could not be prepared safely. {message}");
    }

    private void SynchronizeNotesDirectory()
    {
        var notesDirectory = _getNotesDirectory();
        if (!string.IsNullOrWhiteSpace(notesDirectory))
            _service.UpdateNotesDirectory(notesDirectory);
    }

    private void Unregister(PreparationRegistration registration)
    {
        registration.Deactivate();
        _preparationRegistrations.Remove(registration);
    }

    private sealed record RestoreRequest(
        string? ArchivePath,
        Guid? BackupId,
        string? VerificationToken);

    private sealed class PreparationRegistration : IDisposable
    {
        private readonly BackupCoordinator _owner;
        private readonly IDisposable? _shutdownRegistration;
        private Func<PersistenceSaveResult>? _flush;

        internal PreparationRegistration(
            BackupCoordinator owner,
            string participantName,
            BackupPreparationPhase phase,
            Func<PersistenceSaveResult> flush,
            Func<string?> currentIssue,
            Func<bool>? recoveryBlocksBackup,
            string? recoveryIssue,
            bool includeRecoveryInUserBackup,
            int preparationOrder,
            IDisposable? shutdownRegistration)
        {
            _owner = owner;
            ParticipantName = participantName;
            Phase = phase;
            _flush = flush;
            CurrentIssue = currentIssue;
            RecoveryBlocksBackup = recoveryBlocksBackup;
            RecoveryIssue = recoveryIssue;
            IncludeRecoveryInUserBackup = includeRecoveryInUserBackup;
            PreparationOrder = preparationOrder;
            _shutdownRegistration = shutdownRegistration;
        }

        internal string ParticipantName { get; }
        internal BackupPreparationPhase Phase { get; }
        internal Func<string?> CurrentIssue { get; }
        internal Func<bool>? RecoveryBlocksBackup { get; }
        internal string? RecoveryIssue { get; }
        internal bool IncludeRecoveryInUserBackup { get; }
        internal int PreparationOrder { get; }
        internal bool IsActive => _flush is not null;

        internal PersistenceSaveResult Flush() =>
            _flush?.Invoke() ?? PersistenceSaveResult.Succeeded();

        internal void Deactivate() => _flush = null;

        public void Dispose()
        {
            if (!IsActive)
                return;

            _shutdownRegistration?.Dispose();
            _owner.Unregister(this);
        }
    }
}
