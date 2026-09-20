using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class BackupCoordinatorTests
{
    [TestMethod]
    public void PreparationPreservesParticipantOrderAndDoesNotDoubleFlushSharedParticipants()
    {
        using var directory = new TemporaryTestDirectory();
        var shutdown = new ShutdownFlushCoordinator();
        var coordinator = CreateCoordinator(directory, shutdown);
        var calls = new List<string>();

        using var scratchpad = coordinator.RegisterPreparationParticipant(
            "Scratchpad",
            BackupPreparationPhase.BeforeSharedFlush,
            () => Save("Scratchpad"),
            () => null,
            preparationOrder: 1);
        using var miniPad = coordinator.RegisterPreparationParticipant(
            "MiniPad",
            BackupPreparationPhase.BeforeSharedFlush,
            () => Save("MiniPad"),
            () => null,
            preparationOrder: 0);
        using var checklist = coordinator.RegisterPreparationParticipant(
            "Checklist",
            BackupPreparationPhase.SharedFlush,
            () => Save("Checklist"),
            () => "Checklist warning.");
        using var dictionary = coordinator.RegisterPreparationParticipant(
            "Dictionary",
            BackupPreparationPhase.SharedFlush,
            () => Save("Dictionary"),
            () => null);
        using var editor = coordinator.RegisterPreparationParticipant(
            "Note editor",
            BackupPreparationPhase.AfterSharedFlush,
            () => Save("Note editor"),
            () => null);

        var result = coordinator.CreateOrUpdateUserBackup();

        CollectionAssert.AreEqual(
            new[] { "MiniPad", "Scratchpad", "Checklist", "Dictionary", "Note editor" },
            calls);
        Assert.AreEqual(FullBackupStatus.Blocked, result.Status);
        StringAssert.Contains(result.Message, "Checklist warning.");

        PersistenceSaveResult Save(string participant)
        {
            calls.Add(participant);
            return PersistenceSaveResult.Succeeded();
        }
    }

    [TestMethod]
    public void DisposedRegistrationStopsBackupAndShutdownParticipation()
    {
        using var directory = new TemporaryTestDirectory();
        var shutdown = new ShutdownFlushCoordinator();
        var coordinator = CreateCoordinator(directory, shutdown);
        var calls = 0;
        var registration = coordinator.RegisterPreparationParticipant(
            "Checklist",
            BackupPreparationPhase.SharedFlush,
            () =>
            {
                calls++;
                return PersistenceSaveResult.Succeeded();
            },
            () => null);

        registration.Dispose();
        registration.Dispose();
        _ = coordinator.CreateOrUpdateUserBackup();
        _ = shutdown.FlushAll();

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void ManualBackupIgnoresRunHistoryButCanIncludeParticipantRecoveryGate()
    {
        using var directory = new TemporaryTestDirectory();
        CreateCurrentData(directory);
        var coordinator = CreateCoordinator(directory, new ShutdownFlushCoordinator());
        coordinator.RecordRunRecoveryIssue("Earlier settings recovery.");
        using var ordinaryRecovery = coordinator.RegisterPreparationParticipant(
            "Checklist",
            BackupPreparationPhase.SharedFlush,
            () => PersistenceSaveResult.Succeeded(),
            () => null,
            () => true,
            "Checklist recovery.");

        Assert.AreEqual(FullBackupStatus.Created, coordinator.CreateOrUpdateUserBackup().Status);

        using var secondDirectory = new TemporaryTestDirectory();
        CreateCurrentData(secondDirectory);
        var strictCoordinator = CreateCoordinator(secondDirectory, new ShutdownFlushCoordinator());
        using var editorRecovery = strictCoordinator.RegisterPreparationParticipant(
            "Note editor",
            BackupPreparationPhase.AfterSharedFlush,
            () => PersistenceSaveResult.Succeeded(),
            () => null,
            () => true,
            "Editor recovery.",
            includeRecoveryInUserBackup: true);

        var blocked = strictCoordinator.CreateOrUpdateUserBackup();
        Assert.AreEqual(FullBackupStatus.Blocked, blocked.Status);
        StringAssert.Contains(blocked.Message, "Editor recovery.");
    }

    [TestMethod]
    public void RecentBackupStopsSilentlyWhenRunRecoveryHistoryBlocksIt()
    {
        using var directory = new TemporaryTestDirectory();
        CreateCurrentData(directory);
        var firstRun = CreateCoordinator(directory, new ShutdownFlushCoordinator());
        Assert.AreEqual(FullBackupStatus.Created, firstRun.CreateOrUpdateUserBackup().Status);

        var secondRun = CreateCoordinator(directory, new ShutdownFlushCoordinator());
        var flushes = 0;
        using var participant = secondRun.RegisterPreparationParticipant(
            "Checklist",
            BackupPreparationPhase.SharedFlush,
            () =>
            {
                flushes++;
                return PersistenceSaveResult.Succeeded();
            },
            () => null);
        secondRun.RecordRunRecoveryIssue("Settings recovery was used.");

        Assert.IsNull(secondRun.TryUpdateRecentBackup(TimeSpan.FromHours(24)));
        Assert.AreEqual(1, flushes, "Preparation still flushes current data before applying the run-level gate.");
    }

    [TestMethod]
    public void UserRestoreIntentCanBePreparedCancelledAndRetried()
    {
        using var directory = new TemporaryTestDirectory();
        CreateCurrentData(directory);
        var coordinator = CreateCoordinator(directory, new ShutdownFlushCoordinator());
        Assert.AreEqual(FullBackupStatus.Created, coordinator.CreateOrUpdateUserBackup().Status);
        var shutdownRequests = 0;
        coordinator.ShutdownRequested += (_, _) => shutdownRequests++;

        coordinator.RequestUserBackupRestore();
        var firstPreparation = coordinator.PrepareRequestedRestore();
        Assert.IsTrue(firstPreparation.Success);
        Assert.IsTrue(coordinator.HasRequestedRestore);
        coordinator.CancelRequestedRestore();
        Assert.IsFalse(coordinator.HasRequestedRestore);
        Assert.AreEqual(FullBackupStatus.Skipped, coordinator.ApplyPendingRestore().Status);

        coordinator.RequestUserBackupRestore();
        var retry = coordinator.PrepareRequestedRestore();
        Assert.IsTrue(retry.Success);
        Assert.AreEqual(FullBackupStatus.Restored, coordinator.ApplyPendingRestore().Status);
        Assert.AreEqual(2, shutdownRequests);
    }

    [TestMethod]
    public void UserAndImportFacadePreservesVerificationArguments()
    {
        using var directory = new TemporaryTestDirectory();
        CreateCurrentData(directory);
        var coordinator = CreateCoordinator(directory, new ShutdownFlushCoordinator());
        Assert.AreEqual(FullBackupStatus.Created, coordinator.CreateOrUpdateUserBackup().Status);
        var info = coordinator.GetUserBackupInfo();
        var preview = coordinator.GetUserBackupPreview();
        Assert.IsTrue(info.IsValid);
        Assert.IsTrue(preview.IsValid);

        var note = preview.Files.Single(file => file.LogicalPath.StartsWith("notes/", StringComparison.Ordinal));
        var content = coordinator.ReadUserBackupFile(preview.BackupId, note);
        Assert.IsTrue(content.Success);
        Assert.AreEqual("note one", Encoding.UTF8.GetString(content.Data!));

        var exportPath = directory.File("portable.notedbackup");
        Assert.IsTrue(coordinator.ExportUserBackup(exportPath).Success);
        var importPreview = coordinator.GetBackupImportPreview(exportPath);
        Assert.IsTrue(importPreview.IsValid, importPreview.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(importPreview.VerificationToken));
        var importedContent = coordinator.ReadBackupImportFile(
            exportPath,
            importPreview.BackupId,
            importPreview.VerificationToken!,
            importPreview.Files.Single(file => file.LogicalPath == note.LogicalPath));
        Assert.IsTrue(importedContent.Success, importedContent.Error);
    }

    [TestMethod]
    public void ImportRestoreIntentUsesTheVerifiedArchiveIdentity()
    {
        using var directory = new TemporaryTestDirectory();
        CreateCurrentData(directory);
        var coordinator = CreateCoordinator(directory, new ShutdownFlushCoordinator());
        Assert.AreEqual(FullBackupStatus.Created, coordinator.CreateOrUpdateUserBackup().Status);
        var exportPath = directory.File("portable.notedbackup");
        Assert.IsTrue(coordinator.ExportUserBackup(exportPath).Success);
        var preview = coordinator.GetBackupImportPreview(exportPath);
        Assert.IsTrue(preview.IsValid, preview.Error);

        coordinator.RequestBackupImportRestore(
            exportPath,
            preview.BackupId,
            preview.VerificationToken!);

        var preparation = coordinator.PrepareRequestedRestore();
        Assert.IsTrue(preparation.Success, preparation.ScheduleResult?.Message);
        Assert.AreEqual(FullBackupStatus.Restored, coordinator.ApplyPendingRestore().Status);
    }

    [TestMethod]
    public void InvalidRestoreRequestsAreRejectedBeforeChangingIntent()
    {
        using var directory = new TemporaryTestDirectory();
        var coordinator = CreateCoordinator(directory, new ShutdownFlushCoordinator());

        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.PrepareRequestedRestore());
        Assert.ThrowsExactly<ArgumentException>(() =>
            coordinator.RequestBackupImportRestore("archive.notedbackup", Guid.Empty, "token"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            coordinator.RequestBackupImportRestore("archive.notedbackup", Guid.NewGuid(), " "));
        Assert.IsFalse(coordinator.HasRequestedRestore);
    }

    private static BackupCoordinator CreateCoordinator(
        TemporaryTestDirectory directory,
        ShutdownFlushCoordinator shutdown)
    {
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        return new BackupCoordinator(
            new FullBackupService(appData, directory.File("stale-notes-root")),
            shutdown,
            () => notes);
    }

    private static void CreateCurrentData(TemporaryTestDirectory directory)
    {
        var appData = directory.File("app-data");
        var notes = directory.File("notes");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "one.txt"), "note one");
        File.WriteAllText(
            Path.Combine(appData, "settings.json"),
            "{\"SchemaVersion\":1,\"Revision\":1}");
    }
}
