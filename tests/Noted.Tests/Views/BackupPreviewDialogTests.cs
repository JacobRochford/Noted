using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;
using Noted.Tests.Services;

namespace Noted.Tests.Views;

[TestClass]
public sealed class BackupPreviewDialogTests
{
    [TestMethod]
    public void BeforeRestoreBrowserHasExportButNoRestoreAction()
    {
        using var directory = new TemporaryTestDirectory();
        var service = new FullBackupService(directory.File("app"), directory.File("notes"));
        var coordinator = new BackupCoordinator(service, new ShutdownFlushCoordinator(), () => directory.File("notes"));
        WpfTestHost.Invoke(() =>
        {
            var dialog = new BackupPreviewDialog(coordinator);
            try
            {
                Assert.AreEqual(Visibility.Collapsed, ((Button)dialog.FindName("RestoreBackupButton")).Visibility);
                var export = (Button)dialog.FindName("ExportFileButton");
                Assert.AreEqual(Visibility.Visible, export.Visibility);
                Assert.IsFalse(export.IsEnabled);
                Assert.AreEqual(Visibility.Visible, ((StackPanel)dialog.FindName("SnapshotPanel")).Visibility);
                Assert.IsFalse(coordinator.HasRequestedRestore);
            }
            finally { dialog.Close(); }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UserAndImportedPreviewsKeepTheirRestoreActions(bool imported)
    {
        WpfTestHost.Invoke(() =>
        {
            var preview = new FullBackupPreview(true, true, Guid.NewGuid(), DateTime.UtcNow, [], null);
            var dialog = new BackupPreviewDialog(preview, (_, _) => throw new AssertFailedException("No file is selected."), imported);
            try
            {
                var restore = (Button)dialog.FindName("RestoreBackupButton");
                Assert.AreEqual(Visibility.Visible, restore.Visibility);
                Assert.IsTrue(restore.IsEnabled);
                Assert.AreEqual(imported ? "Restore Imported Backup" : "Restore Full Backup", restore.Content);
                Assert.AreEqual(Visibility.Collapsed, ((Button)dialog.FindName("ExportFileButton")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((StackPanel)dialog.FindName("SnapshotPanel")).Visibility);
                StringAssert.Contains(((TextBlock)dialog.FindName("BackupSummaryText")).Text, "restore locations");
            }
            finally { dialog.Close(); }
        });
    }
}
