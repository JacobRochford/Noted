using Noted.Services;

namespace Noted.Tests.Services;

internal sealed class ControlledHotkeyService : IHotkeyRegistrationService
{
    internal List<string> Attempts { get; } = [];
    internal HashSet<string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HotkeyReplacementResult? Replacement { get; set; }
    internal int ReplacementCalls { get; private set; }
    internal Dictionary<string, Action> Callbacks { get; } = new();
    internal List<int> UnregisteredIds { get; } = [];
    internal Queue<HotkeyUnregistrationResult> UnregistrationResults { get; } = new();
    internal HotkeyUnregistrationResult Unregistration { get; set; } = new(true, "");
    internal int DisposeCalls { get; private set; }
    internal Action? Registering { get; set; }

    public HotkeyValidationResult Validate(string? modifiers, string? key) =>
        string.IsNullOrWhiteSpace(modifiers) || string.IsNullOrWhiteSpace(key)
            ? new(false, null, null, "Invalid selection.")
            : new(true, modifiers, key, "");

    public HotkeyRegistrationResult Register(string featureName, string modifiers, string key, Action callback)
    {
        Attempts.Add($"{modifiers}+{key}");
        Registering?.Invoke();
        Callbacks[featureName] = callback;
        return Unavailable.Contains($"{modifiers}+{key}")
            ? new(false, null, "Already in use.")
            : new(true, new HotkeyRegistration(Attempts.Count, featureName, modifiers, key), "Registered.");
    }

    public HotkeyReplacementResult Replace(string featureName, HotkeyRegistration? currentRegistration,
        string modifiers, string key, Action callback)
    {
        ReplacementCalls++;
        Callbacks[featureName] = callback;
        return Replacement ?? new(true, currentRegistration?.Combination == $"{modifiers}+{key}",
            new HotkeyRegistration(100, featureName, modifiers, key), null, "Replaced.");
    }

    public HotkeyUnregistrationResult Unregister(int id)
    {
        UnregisteredIds.Add(id);
        return UnregistrationResults.Count > 0 ? UnregistrationResults.Dequeue() : Unregistration;
    }

    public void Dispose() => DisposeCalls++;
}
