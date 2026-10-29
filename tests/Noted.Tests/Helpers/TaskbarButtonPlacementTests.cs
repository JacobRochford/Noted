using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class TaskbarButtonPlacementTests
{
    [TestMethod]
    public void RightPlacementLeavesTheNotificationAreaClear()
    {
        var bounds = TaskbarButtonPlacement.CalculateBounds(
            new Rect(0, 1032, 1920, 48), new Rect(0, 0, 1920, 1080),
            new Rect(1600, 1032, 320, 48), new Size(88, 44), false);

        Assert.AreEqual(new Rect(1504, 1034, 88, 44), bounds);
    }

    [TestMethod]
    public void LeftPlacementSupportsNegativeMonitorCoordinatesAndNoTray()
    {
        var bounds = TaskbarButtonPlacement.CalculateBounds(
            new Rect(-1920, 1032, 1920, 48), new Rect(-1920, 0, 1920, 1080),
            Rect.Empty, new Size(88, 44), true);

        Assert.AreEqual(new Rect(-1912, 1034, 88, 44), bounds);
    }

    [TestMethod]
    public void ShortTaskbarFitsTheButtonWithoutChangingItsWidth()
    {
        var bounds = TaskbarButtonPlacement.CalculateBounds(
            new Rect(0, 0, 1280, 32), new Rect(0, 0, 1280, 720),
            new Rect(1080, 0, 200, 32), new Size(88, 44), false);

        Assert.AreEqual(new Rect(984, 2, 88, 28), bounds);
    }

    [TestMethod]
    public void HiddenOrVerticalTaskbarsUseTheDesktopFallback()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var button = new Size(88, 44);
        Assert.IsTrue(TaskbarButtonPlacement.CalculateBounds(
            new Rect(0, 1078, 1920, 48), monitor, Rect.Empty, button, true).IsEmpty);
        Assert.IsTrue(TaskbarButtonPlacement.CalculateBounds(
            new Rect(0, 0, 48, 1080), monitor, Rect.Empty, button, true).IsEmpty);
    }

    [TestMethod]
    public void UnknownOrCrowdedTrayUsesTheDesktopFallbackInsteadOfCoveringControls()
    {
        var taskbar = new Rect(0, 1032, 1920, 48);
        var monitor = new Rect(0, 0, 1920, 1080);
        var button = new Size(88, 44);
        Assert.IsTrue(TaskbarButtonPlacement.CalculateBounds(taskbar, monitor, Rect.Empty, button, false).IsEmpty);
        Assert.IsTrue(TaskbarButtonPlacement.CalculateBounds(
            taskbar, monitor, new Rect(90, 1032, 1830, 48), button, false).IsEmpty);
    }
}
