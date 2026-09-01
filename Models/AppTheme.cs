namespace Noted.Models;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public static class AppTheme
{
    public const string DefaultAccentColor = "#5BA8C8";

    public static string NormalizeAccentColor(string? value) =>
        TryNormalizeAccentColor(value, out var normalized)
            ? normalized
            : DefaultAccentColor;

    public static bool TryNormalizeAccentColor(
        string? value,
        out string normalized)
    {
        var hex = value?.Trim();
        if (hex?.StartsWith('#') == true)
            hex = hex[1..];

        if (hex is { Length: 6 } && hex.All(Uri.IsHexDigit))
        {
            normalized = $"#{hex.ToUpperInvariant()}";
            return true;
        }

        normalized = DefaultAccentColor;
        return false;
    }
}
