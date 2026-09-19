using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ApplicationShutdownSequenceTests
{
    [TestMethod]
    public void SuccessfulShutdownFlushesAndPreparesInOrderBeforeUpdatingBackup()
    {
        var sharedFlushes = new ShutdownFlushCoordinator();
        var sequence = new ApplicationShutdownSequence(sharedFlushes);
        var calls = new List<string>();
        using var shared = sharedFlushes.Register("Shared", () => RecordSuccess(calls, "shared"));

        var result = sequence.Execute(
            [
                new("MiniPad", () => RecordSuccess(calls, "minipad")),
                new("Scratchpad", () => RecordSuccess(calls, "scratchpad"))
            ],
            () =>
            {
                calls.Add("prepare");
                return WindowPreparationResult.Succeeded();
            },
            () => false,
            () => throw new AssertFailedException("Restore preparation must not run."),
            () => calls.Add("cancel"),
            () => calls.Add("backup"));

        Assert.IsTrue(result.CanShutdown);
        CollectionAssert.AreEqual(
            new[] { "minipad", "scratchpad", "shared", "prepare", "backup" },
            calls);
    }

    [TestMethod]
    public void DirectFlushFailureCancelsAndStopsLaterWork()
    {
        var sequence = new ApplicationShutdownSequence(new ShutdownFlushCoordinator());
        var calls = new List<string>();

        var result = sequence.Execute(
            [
                new("MiniPad", () =>
                {
                    calls.Add("minipad");
                    return PersistenceSaveResult.Failed("Disk is full.");
                }),
                new("Scratchpad", () => RecordSuccess(calls, "scratchpad"))
            ],
            () => throw new AssertFailedException("Window preparation must not run."),
            () => false,
            () => throw new AssertFailedException("Restore preparation must not run."),
            () => calls.Add("cancel"),
            () => calls.Add("backup"));

        Assert.IsFalse(result.CanShutdown);
        Assert.AreEqual(ShutdownBlockReason.DirectFlush, result.BlockReason);
        Assert.AreEqual("MiniPad", result.ParticipantName);
        Assert.AreEqual("Disk is full.", result.Error);
        CollectionAssert.AreEqual(new[] { "minipad", "cancel" }, calls);
    }

    [TestMethod]
    public void SharedFlushFailureCancelsBeforeWindowPreparation()
    {
        var sharedFlushes = new ShutdownFlushCoordinator();
        var sequence = new ApplicationShutdownSequence(sharedFlushes);
        var calls = new List<string>();
        using var shared = sharedFlushes.Register("Checklist", () =>
        {
            calls.Add("shared");
            return PersistenceSaveResult.Failed("Save failed.");
        });

        var result = sequence.Execute(
            [],
            () => throw new AssertFailedException("Window preparation must not run."),
            () => false,
            () => throw new AssertFailedException("Restore preparation must not run."),
            () => calls.Add("cancel"),
            () => calls.Add("backup"));

        Assert.AreEqual(ShutdownBlockReason.SharedFlush, result.BlockReason);
        CollectionAssert.AreEqual(new[] { "shared", "cancel" }, calls);
        CollectionAssert.AreEqual(
            new[] { new ShutdownFlushFailure("Checklist", "Save failed.") },
            result.SharedFlushFailures!.ToArray());
    }

    [TestMethod]
    public void WindowPreparationFailureCancelsPreparedStateAndCanBeRetried()
    {
        var sequence = new ApplicationShutdownSequence(new ShutdownFlushCoordinator());
        var attempts = 0;
        var cancellations = 0;

        ApplicationShutdownResult Execute() => sequence.Execute(
            [],
            () => ++attempts == 1
                ? WindowPreparationResult.Failed("Settings are locked.")
                : WindowPreparationResult.Succeeded(),
            () => false,
            () => throw new AssertFailedException("Restore preparation must not run."),
            () => cancellations++,
            () => { });

        var failed = Execute();
        var retried = Execute();

        Assert.AreEqual(ShutdownBlockReason.WindowPreparation, failed.BlockReason);
        Assert.AreEqual("Settings are locked.", failed.Error);
        Assert.AreEqual(1, cancellations);
        Assert.IsTrue(retried.CanShutdown);
    }

    [TestMethod]
    public void RequestedRestoreRunsAfterWindowPreparationAndSkipsRecentBackup()
    {
        var sequence = new ApplicationShutdownSequence(new ShutdownFlushCoordinator());
        var calls = new List<string>();
        var preparation = new BackupRestorePreparationResult(
            BackupResult(FullBackupStatus.Created, "Protected.", "Protection warning."),
            BackupResult(FullBackupStatus.RestoreScheduled, "Scheduled."));

        var result = sequence.Execute(
            [],
            () =>
            {
                calls.Add("prepare");
                return WindowPreparationResult.Succeeded();
            },
            () => true,
            () =>
            {
                calls.Add("restore");
                return preparation;
            },
            () => calls.Add("cancel"),
            () => calls.Add("backup"));

        Assert.IsTrue(result.CanShutdown);
        Assert.AreEqual("Protection warning.", result.Warning);
        CollectionAssert.AreEqual(new[] { "prepare", "restore" }, calls);
    }

    [TestMethod]
    public void RestoreSchedulingFailureCancelsAfterPreservingProtectionWarning()
    {
        var sequence = new ApplicationShutdownSequence(new ShutdownFlushCoordinator());
        var cancellations = 0;
        var preparation = new BackupRestorePreparationResult(
            BackupResult(FullBackupStatus.Created, "Protected.", "Protection warning."),
            BackupResult(FullBackupStatus.Failed, "Schedule failed."));

        var result = sequence.Execute(
            [],
            WindowPreparationResult.Succeeded,
            () => true,
            () => preparation,
            () => cancellations++,
            () => Assert.Fail("Recent backup must not run during restore."));

        Assert.AreEqual(ShutdownBlockReason.RestoreScheduling, result.BlockReason);
        Assert.AreEqual("Schedule failed.", result.Error);
        Assert.AreEqual("Protection warning.", result.Warning);
        Assert.AreEqual(1, cancellations);
    }

    private static PersistenceSaveResult RecordSuccess(ICollection<string> calls, string call)
    {
        calls.Add(call);
        return PersistenceSaveResult.Succeeded();
    }

    private static FullBackupResult BackupResult(
        FullBackupStatus status,
        string message,
        string? warning = null) =>
        new(status, FullBackupType.BeforeRestore, null, message, warning);
}
