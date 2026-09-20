namespace Noted.Models;

public sealed record ChecklistPriorityColors
{
    public string HighColor { get; init; } = ChecklistColors.DefaultHigh;
    public string MediumColor { get; init; } = ChecklistColors.DefaultMedium;
    public string LowColor { get; init; } = ChecklistColors.DefaultLow;
}

public sealed record ChecklistColorChoice(string Name, string Hex);

public static class ChecklistColors
{
    public const string DefaultHigh = "#FF0000";
    public const string DefaultMedium = "#FFA500";
    public const string DefaultLow = "#0000FF";

    public static IReadOnlyList<ChecklistColorChoice> Choices { get; } =
    [
        new("Red", "#FF0000"),
        new("Green", "#008000"),
        new("Lime", "#00FF00"),
        new("Blue", "#0000FF"),
        new("Yellow", "#FFFF00"),
        new("Cyan / Aqua", "#00FFFF"),
        new("Magenta / Fuchsia", "#FF00FF"),
        new("Orange", "#FFA500"),
        new("Purple", "#800080"),
        new("Violet", "#EE82EE"),
        new("Indigo", "#4B0082"),
        new("Pink", "#FFC0CB"),
        new("Hot Pink", "#FF69B4"),
        new("Deep Pink", "#FF1493"),
        new("Maroon", "#800000"),
        new("Teal", "#008080"),
        new("Navy", "#000080"),
        new("Olive", "#808000"),
        new("Gold", "#FFD700"),
        new("Silver", "#C0C0C0"),
        new("Gray", "#808080"),
        new("Black", "#000000")
    ];

    public static ChecklistPriorityColors Normalize(ChecklistPriorityColors? colors)
    {
        colors ??= new ChecklistPriorityColors();
        return colors with
        {
            HighColor = NormalizeColor(colors.HighColor, DefaultHigh),
            MediumColor = NormalizeColor(colors.MediumColor, DefaultMedium),
            LowColor = NormalizeColor(colors.LowColor, DefaultLow)
        };
    }

    private static string NormalizeColor(string? color, string fallback)
    {
        return Choices.FirstOrDefault(choice =>
                string.Equals(choice.Hex, color, StringComparison.OrdinalIgnoreCase))
            ?.Hex ?? fallback;
    }
}
