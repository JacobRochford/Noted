using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

public sealed partial class FullBackupServiceTests
{
    [TestMethod]
    public void BackupExportAndImportPreserveMultipleMiniPadIdsAndClosedContent()
    {
        using var sourceDirectory = new TemporaryTestDirectory();
        using var targetDirectory = new TemporaryTestDirectory();
        var source = CreateSourceData(sourceDirectory);
        var settings = new AppSettingsService(source.AppData);
        var ids = new[] { MiniPadRecoveryService.LegacyDraftId, Guid.NewGuid(), Guid.NewGuid() };
        for (var index = 0; index < ids.Length; index++)
        {
            settings.SaveMiniPadWindowState(ids[index], new MiniPadWindowState
            {
                Left = 100 + index * 50, Top = 100, Width = 340, Height = 260,
                IsClosed = index == 1, ReopenOnStartup = index != 1
            });
            new MiniPadRecoveryService(source.AppData, ids[index]).SaveDraft($"content {index}");
        }
        var service = new FullBackupService(source.AppData, source.Notes);
        Assert.AreEqual(FullBackupStatus.Created, service.CreateOrUpdateUserBackup().Status);
        var exportPath = sourceDirectory.File("minipads.notedbackup");
        Assert.IsTrue(service.ExportUserBackup(exportPath).Success);
        var target = CreateSourceData(targetDirectory);
        var importer = new FullBackupService(target.AppData, target.Notes);

        var preview = importer.GetBackupImportPreview(exportPath);

        Assert.IsTrue(preview.IsValid, preview.Error);
        for (var index = 0; index < ids.Length; index++)
        {
            var file = preview.Files.Single(file => file.LogicalPath == $"app/recovery/minipads/{ids[index]:N}.json");
            var content = importer.ReadBackupImportFile(exportPath, preview.BackupId, preview.VerificationToken!, file);
            Assert.IsTrue(content.Success, content.Error);
            var draft = JsonSerializer.Deserialize<MiniPadRecoveryDraft>(content.Data!)!;
            Assert.AreEqual(ids[index], draft.Id);
            Assert.AreEqual($"content {index}", draft.Content);
        }
        var settingsFile = preview.Files.Single(file => file.LogicalPath == "app/settings.json");
        var importedSettings = importer.ReadBackupImportFile(exportPath, preview.BackupId, preview.VerificationToken!, settingsFile);
        var document = JsonSerializer.Deserialize<AppSettingsDocument>(importedSettings.Data!)!;
        Assert.IsNotNull(document.MiniPadWindows);
        Assert.HasCount(3, document.MiniPadWindows);
        Assert.IsTrue(document.MiniPadWindows[ids[1]].IsClosed);

        Assert.AreEqual(FullBackupStatus.RestoreScheduled,
            importer.ScheduleBackupImportRestore(exportPath, preview.BackupId, preview.VerificationToken!).Status);
        Assert.AreEqual(FullBackupStatus.Restored, importer.ApplyPendingUserBackupRestore().Status);
        for (var index = 0; index < ids.Length; index++)
            Assert.AreEqual($"content {index}", new MiniPadRecoveryService(target.AppData, ids[index]).LoadDraft().Draft!.Content);
        Assert.IsTrue(new AppSettingsService(target.AppData).LoadMiniPadWindows()[ids[1]].IsClosed);
    }
}
