using Noted.Models;

namespace Noted.Tests.Models;

[TestClass]
public sealed class ChecklistColorsTests
{
    [TestMethod]
    public void Normalize_KeepsSupportedColors()
    {
        var colors = ChecklistColors.Normalize(new ChecklistPriorityColors
        {
            HighColor = "#800000",
            MediumColor = "#FFD700",
            LowColor = "#008080"
        });

        Assert.AreEqual("#800000", colors.HighColor);
        Assert.AreEqual("#FFD700", colors.MediumColor);
        Assert.AreEqual("#008080", colors.LowColor);
    }

    [TestMethod]
    public void Normalize_ReplacesUnknownColorsWithDefaults()
    {
        var colors = ChecklistColors.Normalize(new ChecklistPriorityColors
        {
            HighColor = "#123456",
            MediumColor = "not-a-color",
            LowColor = ""
        });

        Assert.AreEqual(ChecklistColors.DefaultHigh, colors.HighColor);
        Assert.AreEqual(ChecklistColors.DefaultMedium, colors.MediumColor);
        Assert.AreEqual(ChecklistColors.DefaultLow, colors.LowColor);
    }

    [TestMethod]
    public void Palette_ContainsEverySupportedColorOnce()
    {
        Assert.HasCount(22, ChecklistColors.Choices);
        Assert.HasCount(
            22,
            ChecklistColors.Choices
                .Select(choice => choice.Hex)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }
}
