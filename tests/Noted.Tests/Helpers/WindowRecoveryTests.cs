using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using Noted.Helpers;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class WindowRecoveryTests
{
    [TestMethod]
    public void RestoreShowsMinimizedOffScreenWindowInsideWorkArea()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                window.Left = -100000;
                window.Top = -100000;
                window.WindowState = WindowState.Minimized;
                window.Hide();

                WindowInterop.RestoreWindow(window, WindowState.Normal);

                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(WindowState.Normal, window.WindowState);
                Assert.IsTrue(SystemParameters.WorkArea.Contains(
                    new Rect(window.Left, window.Top, window.Width, window.Height)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RestorePreservesMaximizedStateAndNormalBounds()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = CreateWindow();
            try
            {
                WindowInterop.RestoreWindow(window, WindowState.Normal);
                var normalBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
                window.WindowState = WindowState.Maximized;
                window.WindowState = WindowState.Minimized;

                WindowInterop.RestoreWindow(window, WindowState.Maximized);

                Assert.AreEqual(WindowState.Maximized, window.WindowState);
                Assert.AreEqual(normalBounds, window.RestoreBounds);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Window CreateWindow() => new()
    {
        Width = 320,
        Height = 240,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual
    };
}
