namespace Noted.Services;

internal enum ShutdownBlockReason
{
    DirectFlush,
    SharedFlush,
    WindowPreparation,
    RestoreProtection,
    RestoreScheduling
}

internal sealed record ShutdownDirectFlush(
    string ParticipantName,
    Func<PersistenceSaveResult> Flush);

internal readonly record struct WindowPreparationResult(bool Success, string? Error)
{
    internal static WindowPreparationResult Succeeded() => new(true, null);
    internal static WindowPreparationResult Failed(string? error = null) => new(false, error);
}

internal sealed record ApplicationShutdownResult(
    bool CanShutdown,
    ShutdownBlockReason? BlockReason = null,
    string? ParticipantName = null,
    string? Error = null,
    IReadOnlyList<ShutdownFlushFailure>? SharedFlushFailures = null,
    string? Warning = null)
{
    internal static ApplicationShutdownResult Succeeded(string? warning = null) =>
        new(true, Warning: warning);

    internal static ApplicationShutdownResult Blocked(
        ShutdownBlockReason reason,
        string? participantName = null,
        string? error = null,
        IReadOnlyList<ShutdownFlushFailure>? sharedFlushFailures = null,
        string? warning = null) =>
        new(false, reason, participantName, error, sharedFlushFailures, warning);
}

internal sealed class ApplicationShutdownSequence
{
    private readonly ShutdownFlushCoordinator _sharedFlushCoordinator;

    internal ApplicationShutdownSequence(ShutdownFlushCoordinator sharedFlushCoordinator)
    {
        ArgumentNullException.ThrowIfNull(sharedFlushCoordinator);
        _sharedFlushCoordinator = sharedFlushCoordinator;
    }

    internal ApplicationShutdownResult Execute(
        IEnumerable<ShutdownDirectFlush> directFlushes,
        Func<WindowPreparationResult> prepareWindows,
        Func<bool> hasRequestedRestore,
        Func<BackupRestorePreparationResult> prepareRequestedRestore,
        Action cancelPreparedShutdown,
        Action updateRecentBackup)
    {
        ArgumentNullException.ThrowIfNull(directFlushes);
        ArgumentNullException.ThrowIfNull(prepareWindows);
        ArgumentNullException.ThrowIfNull(hasRequestedRestore);
        ArgumentNullException.ThrowIfNull(prepareRequestedRestore);
        ArgumentNullException.ThrowIfNull(cancelPreparedShutdown);
        ArgumentNullException.ThrowIfNull(updateRecentBackup);

        foreach (var directFlush in directFlushes)
        {
            ArgumentNullException.ThrowIfNull(directFlush);
            var result = directFlush.Flush();
            if (result.Success)
                continue;

            cancelPreparedShutdown();
            return ApplicationShutdownResult.Blocked(
                ShutdownBlockReason.DirectFlush,
                directFlush.ParticipantName,
                result.Error);
        }

        var sharedFailures = _sharedFlushCoordinator.FlushAll();
        if (sharedFailures.Count > 0)
        {
            cancelPreparedShutdown();
            return ApplicationShutdownResult.Blocked(
                ShutdownBlockReason.SharedFlush,
                sharedFlushFailures: sharedFailures);
        }

        var windowPreparation = prepareWindows();
        if (!windowPreparation.Success)
        {
            cancelPreparedShutdown();
            return ApplicationShutdownResult.Blocked(
                ShutdownBlockReason.WindowPreparation,
                error: windowPreparation.Error);
        }

        if (!hasRequestedRestore())
        {
            updateRecentBackup();
            return ApplicationShutdownResult.Succeeded();
        }

        var restorePreparation = prepareRequestedRestore();
        var protectionResult = restorePreparation.ProtectionResult;
        if (protectionResult.Status is not (FullBackupStatus.Created or FullBackupStatus.Skipped))
        {
            cancelPreparedShutdown();
            return ApplicationShutdownResult.Blocked(
                ShutdownBlockReason.RestoreProtection,
                error: protectionResult.Message);
        }

        var warning = protectionResult.Warning;
        var scheduleResult = restorePreparation.ScheduleResult!;
        if (scheduleResult.Status != FullBackupStatus.RestoreScheduled)
        {
            cancelPreparedShutdown();
            return ApplicationShutdownResult.Blocked(
                ShutdownBlockReason.RestoreScheduling,
                error: scheduleResult.Message,
                warning: warning);
        }

        return ApplicationShutdownResult.Succeeded(warning);
    }
}
