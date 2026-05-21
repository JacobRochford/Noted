namespace Noted.Helpers;

using System.Text.RegularExpressions;

public static class HotkeyConstants
{
    // Valid modifier names
    public static readonly List<string> ValidModifiers = new() { "Ctrl", "Alt", "Shift", "Win" };

    // Valid key options (no special characters - alphanumeric and function keys only)
    public static readonly List<string> ValidKeys = new()
    {
        "Space", "Tab", "Enter", "Escape",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Home", "End", "PageUp", "PageDown", "Insert", "Delete",
        "Left", "Right", "Up", "Down",
        "Print", "Pause"
    };

    /// <summary>
    /// Builds a hotkey string from selected modifiers and key
    /// </summary>
    public static string? BuildHotkey(List<string> selectedModifiers, string? selectedKey)
    {
        if (string.IsNullOrWhiteSpace(selectedKey))
            return null;

        if (!selectedModifiers.Any())
            return null;

        // Validate key is in our whitelist
        if (!ValidKeys.Contains(selectedKey))
            return null;

        // Validate all modifiers are valid
        if (!selectedModifiers.All(m => ValidModifiers.Contains(m)))
            return null;

        return $"{string.Join("+", selectedModifiers)}+{selectedKey}";
    }
}

