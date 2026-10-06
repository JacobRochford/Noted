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
    public void ThemeSwitchingPreservesAccentAndSeparatesSurfaces()
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(resources, AppThemeMode.Dark, "#7A5CB8");
        foreach (var theme in new[] { AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.Midnight, AppThemeMode.OLED, AppThemeMode.Warm })
        {
            manager.Apply(theme, manager.AccentColor);
            Assert.AreEqual("#7A5CB8", manager.AccentColor);
            Assert.AreEqual(Color.FromRgb(0x7A, 0x5C, 0xB8), GetColor(resources, "NotedAccentBrush"));
            var surfaces = new[] { "NotedWindowBackgroundBrush", "NotedSurfaceBrush", "NotedEditorBackgroundBrush", "NotedSubtleSurfaceBrush" };
            Assert.AreEqual(4, surfaces.Select(key => GetColor(resources, key)).Distinct().Count());
            foreach (var key in surfaces)
                Assert.IsGreaterThanOrEqualTo(4.5,
                    AppThemeManager.Contrast(GetColor(resources, "NotedTextBrush"), GetColor(resources, key)));
            var danger = GetColor(resources, "NotedDangerBrush");
            var warning = GetColor(resources, "NotedWarningBrush");
            manager.Apply(theme, "#00FF00");
            Assert.AreEqual(Color.FromRgb(0, 255, 0), GetColor(resources, "NotedAccentBrush"));
            Assert.AreEqual(danger, GetColor(resources, "NotedDangerBrush"));
            Assert.AreEqual(warning, GetColor(resources, "NotedWarningBrush"));
            manager.Apply(theme, "#7A5CB8");
        }
    }

    [TestMethod]
    public void SelectionRemainsDistinctAndReadableForExtremeAccentsInEveryTheme()
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(resources, AppThemeMode.Light, "#FFFFFF");
        foreach (var theme in new[] { AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.Midnight, AppThemeMode.OLED, AppThemeMode.Warm })
        foreach (var accent in new[] { "#FFFFFF", "#000000", "#FF0000", "#FFFF00", "#5BA8C8" })
        {
            manager.Apply(theme, accent);
            var selection = GetColor(resources, "NotedSelectionBrush");
            var selectionText = GetColor(resources, "NotedSelectionTextBrush");
            Assert.IsGreaterThanOrEqualTo(3.0, AppThemeManager.Contrast(selection, GetColor(resources, "NotedInputBackgroundBrush")));
            Assert.IsGreaterThanOrEqualTo(3.0, AppThemeManager.Contrast(selection, GetColor(resources, "NotedEditorBackgroundBrush")));
            Assert.IsGreaterThanOrEqualTo(4.5, AppThemeManager.Contrast(selection, selectionText));
            Assert.AreEqual(selection, GetColor(resources, SystemColors.InactiveSelectionHighlightBrushKey));
            Assert.AreEqual(selectionText, GetColor(resources, SystemColors.InactiveSelectionHighlightTextBrushKey));
        }
    }

    [TestMethod]
    [DataRow("#FFFFFF")]
    [DataRow("#000000")]
    [DataRow("#15FFFF")]
    [DataRow("#3366AA")]
    public void LightSurfacesKeepTextAndControlOutlinesReadableWithCustomAccents(string accent)
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(resources, AppThemeMode.Light, accent);
        var surfaceKeys = new[]
        {
            "NotedWindowBackgroundBrush", "NotedSurfaceBrush", "NotedSubtleSurfaceBrush",
            "NotedInputBackgroundBrush", "NotedEditorBackgroundBrush"
        };
        foreach (var surfaceKey in surfaceKeys)
        {
            var surface = GetColor(resources, surfaceKey);
            foreach (var textKey in new[] { "NotedTextBrush", "NotedSecondaryTextBrush", "NotedMutedTextBrush" })
            {
                Assert.IsGreaterThanOrEqualTo(4.5,
                    AppThemeManager.Contrast(GetColor(resources, textKey), surface),
                    $"{textKey} must remain readable on {surfaceKey} with accent {accent}.");
            }
            Assert.IsGreaterThanOrEqualTo(3.0,
                AppThemeManager.Contrast(GetColor(resources, "NotedStrongBorderBrush"), surface),
                $"Control outlines must remain distinct on {surfaceKey} with accent {accent}.");
        }

        Assert.IsGreaterThanOrEqualTo(1.05,
            AppThemeManager.Contrast(GetColor(resources, "NotedEditorBackgroundBrush"),
                GetColor(resources, "NotedWindowBackgroundBrush")));
        Assert.IsGreaterThanOrEqualTo(1.05,
            AppThemeManager.Contrast(GetColor(resources, "NotedSurfaceBrush"),
                GetColor(resources, "NotedSubtleSurfaceBrush")));
        Assert.AreEqual(accent, manager.AccentColor);
        Assert.AreEqual((Color)ColorConverter.ConvertFromString(accent), GetColor(resources, "NotedAccentBrush"));
    }

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
        Assert.AreEqual(Color.FromRgb(0x1B, 0x1D, 0x1F), GetColor(resources, "NotedWindowBackgroundBrush"));
        Assert.AreNotEqual(lightWindow, GetColor(resources, "NotedWindowBackgroundBrush"));
        Assert.IsTrue(((SolidColorBrush)resources["NotedAccentBrush"]).IsFrozen);
    }

    [TestMethod]
    public void SearchHighlightResources_FollowTheAccentAndRemainVisuallyDistinct()
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(
            resources,
            AppThemeMode.Dark,
            "#3366AA");

        var match = GetColor(resources, "NotedSearchMatchBrush");
        var current = GetColor(resources, "NotedSearchCurrentBrush");
        var currentLine = GetColor(resources, "NotedSearchCurrentLineBrush");

        Assert.AreEqual(Color.FromRgb(0x33, 0x66, 0xAA), Color.FromRgb(match.R, match.G, match.B));
        Assert.AreEqual(match.R, current.R);
        Assert.AreEqual(match.G, current.G);
        Assert.AreEqual(match.B, current.B);
        Assert.IsTrue(current.A > match.A);
        Assert.IsTrue(match.A > currentLine.A);
        Assert.AreEqual(
            GetColor(resources, "NotedSelectionBrush"),
            GetColor(resources, "NotedSearchCurrentBorderBrush"));
    }

    [TestMethod]
    public void MidnightKeepsTheBlueDarkPalette()
    {
        var resources = new ResourceDictionary();
        using var manager = new AppThemeManager(
            resources,
            AppThemeMode.Midnight,
            AppTheme.DefaultAccentColor);

        Assert.AreEqual(Color.FromRgb(0x10, 0x1B, 0x21), GetColor(resources, "NotedWindowBackgroundBrush"));
        Assert.AreEqual(Color.FromRgb(0x14, 0x25, 0x2D), GetColor(resources, "NotedSurfaceBrush"));
        Assert.AreEqual(Color.FromRgb(0xE6, 0xF1, 0xF5), GetColor(resources, "NotedTextBrush"));
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

    private static Color GetColor(ResourceDictionary resources, object key) =>
        ((SolidColorBrush)resources[key]).Color;
}
