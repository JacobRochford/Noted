using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Noted.Helpers;
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
        var effectiveMode = _mode == AppThemeMode.System
            ? SystemUsesDarkMode() ? AppThemeMode.Dark : AppThemeMode.Light
            : _mode;
        var dark = effectiveMode != AppThemeMode.Light;
        // Match the existing shared roles, including raised chrome and editor surfaces.
        var (windowHex, surfaceHex, subtleSurfaceHex, inputHex, editorHex,
            borderHex, strongBorderHex, textHex, secondaryTextHex, mutedTextHex) = effectiveMode switch
        {
            AppThemeMode.Light => ("#E8EDF0", "#F0F3F5", "#FFFFFF", "#F7F8F8", "#F5F6F6",
                "#CED7DC", "#718795", "#24323A", "#526670", "#5B6C76"),
            AppThemeMode.Midnight => ("#101B21", "#14252D", "#19313B", "#17282E", "#17282E",
                "#31505A", "#527386", "#E6F1F5", "#A9C0CA", "#819AA6"),
            AppThemeMode.OLED => ("#000000", "#080A0B", "#171B1D", "#101315", "#101315",
                "#292F32", "#596267", "#E9EEEE", "#AAB6BB", "#8B989D"),
            AppThemeMode.Warm => ("#1B1916", "#24211D", "#383229", "#2B2722", "#302C26",
                "#494238", "#756A5B", "#EEE8DC", "#C2B9AB", "#AAA193"),
            _ => ("#1B1D1F", "#232629", "#2B2F33", "#17191B", "#282828",
                "#41464B", "#5D646B", "#F0F2F4", "#B5BBC1", "#858D95")
        };
        var accent = ParseColor(_accentColor);
        var window = ParseColor(windowHex);
        var surface = ParseColor(surfaceHex);
        var subtleSurface = ParseColor(subtleSurfaceHex);
        var input = ParseColor(inputHex);
        var editor = ParseColor(editorHex);
        var border = ParseColor(borderHex);
        var strongBorder = ParseColor(strongBorderHex);
        var text = ParseColor(textHex);
        var secondaryText = ParseColor(secondaryTextHex);
        var mutedText = ParseColor(mutedTextHex);
        var accentSoft = Mix(accent, window, dark ? 0.76 : 0.82);
        var accentSoftHover = Mix(accent, window, dark ? 0.66 : 0.72);

        SetBrush("NotedWindowBackgroundBrush", window);
        SetBrush("NotedSurfaceBrush", surface);
        SetBrush("NotedSubtleSurfaceBrush", subtleSurface);
        SetBrush("NotedInputBackgroundBrush", input);
        SetBrush("NotedEditorBackgroundBrush", editor);
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
        // Keep selections distinct on both text-entry surfaces for every accent.
        var selection = accent;
        for (var i = 0; i < 20 && (Contrast(selection, input) < 3 || Contrast(selection, editor) < 3); i++)
            selection = Mix(selection, dark ? Colors.White : Colors.Black, 0.12);
        var selectionText = Contrast(selection, Colors.White) >= Contrast(selection, Colors.Black)
            ? Colors.White
            : Colors.Black;
        SetBrush("NotedSelectionBrush", selection);
        SetBrush("NotedSelectionTextBrush", selectionText);
        SetBrush("NotedSearchMatchBrush", WithAlpha(accent, 0x59));
        SetBrush("NotedSearchCurrentBrush", WithAlpha(accent, 0x8C));
        SetBrush("NotedSearchCurrentBorderBrush", selection);
        SetBrush("NotedSearchCurrentLineBrush", WithAlpha(accent, 0x20));
        SetBrush(SystemColors.InactiveSelectionHighlightBrushKey, selection);
        SetBrush(SystemColors.InactiveSelectionHighlightTextBrushKey, selectionText);

        // Status colors deliberately do not follow the user-selected accent.
        SetBrush("NotedDangerBrush", ParseColor(dark ? "#FF8A8A" : "#B84F4F"));
        SetBrush("NotedWarningBrush", ParseColor(dark ? "#F2C572" : "#B8761F"));
        SetBrush("NotedSuccessBrush", ParseColor(dark ? "#78D3A2" : "#347052"));

        ApplyTitleBars(window, text, dark);
    }

    internal static void ApplyTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var background = Application.Current?.Resources["NotedWindowBackgroundBrush"] as SolidColorBrush;
        var text = Application.Current?.Resources["NotedTextBrush"] as SolidColorBrush;
        if (background is null || text is null)
            return;

        WindowInterop.SetTitleBarColors(
            new WindowInteropHelper(window).Handle,
            background.Color,
            text.Color,
            IsDark(background.Color));
    }

    private static void ApplyTitleBars(Color background, Color text, bool dark)
    {
        var application = Application.Current;
        if (application is null)
            return;

        var dispatcher = application.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                _ = dispatcher.BeginInvoke(() => ApplyTitleBars(background, text, dark));
            return;
        }

        foreach (Window window in application.Windows)
        {
            WindowInterop.SetTitleBarColors(
                new WindowInteropHelper(window).Handle,
                background,
                text,
                dark);
        }
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
            ExceptionDiagnostics.Record(ex);
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
            ExceptionDiagnostics.Record(ex);
            return false;
        }
    }

    private void SetBrush(object key, Color color)
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

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static bool UseDarkText(Color background) =>
        ((background.R * 299) + (background.G * 587) + (background.B * 114)) / 1000 >= 150;

    internal static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) => value / 255.0 <= 0.04045
            ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        static double Luminance(Color value) =>
            0.2126 * Channel(value.R) + 0.7152 * Channel(value.G) + 0.0722 * Channel(value.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static bool IsDark(Color background) =>
        ((background.R * 299) + (background.G * 587) + (background.B * 114)) / 1000 < 150;
}
