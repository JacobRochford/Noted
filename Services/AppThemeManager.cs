using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Noted.Models;

namespace Noted.Services;

internal sealed class AppThemeManager : IDisposable
{
    private const string PersonalizeKeyPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly ResourceDictionary _resources;
    private AppThemeMode _mode;
    private string _accentColor;
    private bool _listeningForSystemChanges;
    private bool _disposed;

    internal AppThemeManager(
        ResourceDictionary resources,
        AppThemeMode mode,
        string accentColor)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        _resources = resources;
        _mode = mode;
        _accentColor = AppTheme.NormalizeAccentColor(accentColor);
        ApplyResources();
        TryListenForSystemChanges();
    }

    internal AppThemeMode Mode => _mode;
    internal string AccentColor => _accentColor;

    internal void Apply(AppThemeMode mode, string accentColor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!AppTheme.TryNormalizeAccentColor(accentColor, out var normalizedAccent))
            throw new ArgumentException("The accent color must use six hexadecimal digits.", nameof(accentColor));

        _mode = mode;
        _accentColor = normalizedAccent;
        ApplyResources();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_listeningForSystemChanges)
        {
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            _listeningForSystemChanges = false;
        }
    }

    private void ApplyResources()
    {
        var dark = _mode == AppThemeMode.Dark ||
            (_mode == AppThemeMode.System && SystemUsesDarkMode());
        var accent = ParseColor(_accentColor);
        var window = ParseColor(dark ? "#17242C" : "#F5F9FC");
        var surface = ParseColor(dark ? "#1F303A" : "#FFFFFF");
        var subtleSurface = ParseColor(dark ? "#263A45" : "#F2F8FB");
        var input = ParseColor(dark ? "#192932" : "#FFFFFF");
        var border = ParseColor(dark ? "#3B5664" : "#C9DFEC");
        var strongBorder = ParseColor(dark ? "#527386" : "#87CEEB");
        var text = ParseColor(dark ? "#E6F1F5" : "#355A6E");
        var secondaryText = ParseColor(dark ? "#A9C0CA" : "#78909C");
        var mutedText = ParseColor(dark ? "#819AA6" : "#8BA5B2");
        var accentSoft = Mix(accent, window, dark ? 0.76 : 0.82);
        var accentSoftHover = Mix(accent, window, dark ? 0.66 : 0.72);

        SetBrush("NotedWindowBackgroundBrush", window);
        SetBrush("NotedSurfaceBrush", surface);
        SetBrush("NotedSubtleSurfaceBrush", subtleSurface);
        SetBrush("NotedInputBackgroundBrush", input);
        SetBrush("NotedBorderBrush", border);
        SetBrush("NotedStrongBorderBrush", strongBorder);
        SetBrush("NotedTextBrush", text);
        SetBrush("NotedSecondaryTextBrush", secondaryText);
        SetBrush("NotedMutedTextBrush", mutedText);
        SetBrush("NotedAccentBrush", accent);
        SetBrush("NotedAccentHoverBrush", Mix(accent, Colors.Black, 0.12));
        SetBrush("NotedAccentPressedBrush", Mix(accent, Colors.Black, 0.23));
        SetBrush("NotedAccentSoftBrush", accentSoft);
        SetBrush("NotedAccentSoftHoverBrush", accentSoftHover);
        SetBrush("NotedAccentTextBrush", UseDarkText(accent) ? ParseColor("#18252D") : Colors.White);

        // Status colors deliberately do not follow the user-selected accent.
        SetBrush("NotedDangerBrush", ParseColor(dark ? "#FF8A8A" : "#B84F4F"));
        SetBrush("NotedWarningBrush", ParseColor(dark ? "#F2C572" : "#B8761F"));
        SetBrush("NotedSuccessBrush", ParseColor(dark ? "#78D3A2" : "#347052"));
    }

    private void TryListenForSystemChanges()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            _listeningForSystemChanges = true;
        }
        catch (Exception ex) when (ex is
                   InvalidOperationException or
                   ExternalException or
                   SecurityException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (_disposed || _mode != AppThemeMode.System ||
            e.Category is not (UserPreferenceCategory.Color or
                               UserPreferenceCategory.General or
                               UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            ApplyResources();
        else
            _ = dispatcher.BeginInvoke(ApplyResources);
    }

    private static bool SystemUsesDarkMode()
    {
        try
        {
            return Registry.GetValue(
                PersonalizeKeyPath,
                "AppsUseLightTheme",
                1) is int value && value == 0;
        }
        catch (Exception ex) when (ex is
                   SecurityException or
                   UnauthorizedAccessException or
                   IOException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _resources[key] = brush;
    }

    private static Color ParseColor(string hex) =>
        Color.FromRgb(
            byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static Color Mix(Color first, Color second, double secondWeight)
    {
        var weight = Math.Clamp(secondWeight, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(first.R + ((second.R - first.R) * weight)),
            (byte)Math.Round(first.G + ((second.G - first.G) * weight)),
            (byte)Math.Round(first.B + ((second.B - first.B) * weight)));
    }

    private static bool UseDarkText(Color background) =>
        ((background.R * 299) + (background.G * 587) + (background.B * 114)) / 1000 >= 150;
}
