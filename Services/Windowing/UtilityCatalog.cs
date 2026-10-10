namespace Noted.Services;

internal sealed record UtilityDefinition(string Id, string DisplayName, string Icon,
    double IconFontSize, string IconFontWeight, Action Toggle);

internal static class UtilityCatalog
{
    internal static IReadOnlyList<UtilityDefinition> Utilities { get; } = Array.AsReadOnly(new[]
    {
        new UtilityDefinition(PersistenceKeys.Checklist, "Checklist", "✓", 13, "SemiBold", WindowManager.ToggleChecklist),
        new UtilityDefinition(PersistenceKeys.Dictionary, "Dictionary", "Aa", 10, "Bold", WindowManager.ToggleDictionary),
         new UtilityDefinition(PersistenceKeys.Jots, "Jots", "✦", 12, "SemiBold", WindowManager.ToggleJots),
        new UtilityDefinition(PersistenceKeys.Scratchpad, "Scratchpad", "✎", 13, "SemiBold", WindowManager.ToggleScratchpad),
        new UtilityDefinition(PersistenceKeys.MiniPad, "MiniPad", "▤", 12, "SemiBold", WindowManager.ShowMiniPad)
    });
}
