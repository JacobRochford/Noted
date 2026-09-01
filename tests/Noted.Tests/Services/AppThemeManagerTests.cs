using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class AppThemeManagerTests
{
    [TestMethod]
    public void ApplyUpdatesSharedThemeResources()
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(
            resources,
            AppThemeMode.Light,
            "#FFCC00");
        var lightWindow = GetColor(resources, "NotedWindowBackgroundBrush");

        manager.Apply(AppThemeMode.Dark, "#3366AA");

        Assert.AreEqual("#3366AA", manager.AccentColor);
        Assert.AreEqual(AppThemeMode.Dark, manager.Mode);
        Assert.AreEqual(Color.FromRgb(0x33, 0x66, 0xAA), GetColor(resources, "NotedAccentBrush"));
        Assert.AreNotEqual(lightWindow, GetColor(resources, "NotedWindowBackgroundBrush"));
        Assert.IsTrue(((SolidColorBrush)resources["NotedAccentBrush"]).IsFrozen);
    }

    [TestMethod]
    public void StatusColorsDoNotFollowTheAccentColor()
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(
            resources,
            AppThemeMode.Light,
            "#FF0000");
        var danger = GetColor(resources, "NotedDangerBrush");
        var warning = GetColor(resources, "NotedWarningBrush");

        manager.Apply(AppThemeMode.Light, "#0000FF");

        Assert.AreEqual(danger, GetColor(resources, "NotedDangerBrush"));
        Assert.AreEqual(warning, GetColor(resources, "NotedWarningBrush"));
    }

    private static Color GetColor(ResourceDictionary resources, string key) =>
        ((SolidColorBrush)resources[key]).Color;
}
