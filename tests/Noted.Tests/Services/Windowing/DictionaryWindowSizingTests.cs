using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
[TestCategory("DesktopInteraction")]
public sealed class DictionaryWindowSizingTests
{
    [TestMethod]
    public void MaximizeRestoresNormalBoundsAndResetSizeRecoversOversizedBounds() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        settings.SaveDictionaryWindowState(new DictionaryWindowState
        {
            Left = 100,
            Top = 100,
            Width = 480,
            Height = 550
        });
        var window = new DictionaryWindow(settings, new DictionaryContentService(directory.Path));
        try
        {
            window.Show();
            var button = (Button)window.FindName("MaximizeRestoreButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.AreEqual(WindowState.Maximized, window.WindowState);
            var maximized = new AppSettingsService(directory.Path).LoadDictionaryWindowState();
            Assert.IsTrue(maximized.IsMaximized);
            Assert.AreEqual(480, maximized.Width, 2);
            Assert.AreEqual(550, maximized.Height, 2);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            Assert.AreEqual(480, window.Width, 2);
            Assert.AreEqual(550, window.Height, 2);

            window.Width = 900;
            window.Height = 700;
            var titleBar = (Border)window.FindName("TitleBar");
            var menu = (ContextMenu)titleBar.FindResource("TitleBarMenu");
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.AreEqual(WindowState.Normal, window.WindowState);
            Assert.AreEqual(400, window.Width);
            Assert.AreEqual(500, window.Height);
            var reset = new AppSettingsService(directory.Path).LoadDictionaryWindowState();
            Assert.IsFalse(reset.IsMaximized);
            Assert.AreEqual(400, reset.Width);
            Assert.AreEqual(500, reset.Height);
        }
        finally
        {
            window.Close();
        }
    });
}
