using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Noted.Services;

internal sealed record HotkeyRegistration(
    int Id,
    string FeatureName,
    string Modifiers,
    string Key)
{
    public string Combination => $"{Modifiers}+{Key}";
}

internal sealed record HotkeyValidationResult(
    bool Success,
    string? Modifiers,
    string? Key,
    string Description)
{
    public string? Combination => Success ? $"{Modifiers}+{Key}" : null;
}

internal sealed record HotkeyRegistrationResult(
    bool Success,
    HotkeyRegistration? Registration,
    string? RequestedModifiers,
    string? RequestedKey,
    bool InProcessConflict,
    string? ConflictingFeature,
    bool OperatingSystemRejected,
    int? NativeError,
    string Description);

internal sealed record HotkeyUnregistrationResult(
    bool Success,
    bool AlreadyAbsent,
    HotkeyRegistration? Registration,
    int? NativeError,
    string Description);

internal sealed record HotkeyReplacementResult(
    bool Success,
    bool NoChange,
    HotkeyRegistration? ActiveRegistration,
    HotkeyRegistration? AdditionalActiveRegistration,
    HotkeyRegistrationResult? NewRegistrationResult,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    int? PreviousUnregisterError,
    int? NewRegistrationRollbackError,
    string Description)
{
    public bool HasDualActiveRegistrations =>
        ActiveRegistration is not null && AdditionalActiveRegistration is not null;
}

public sealed class GlobalHotkeysService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _hwndSource;
    private readonly Dictionary<int, OwnedRegistration> _registrationsById = new();
    private readonly Dictionary<HotkeyCombination, int> _registrationIdsByCombination = new();
    private int _nextRegistrationId = 9000;
    private bool _hookAttached;
    private bool _disposed;

    private readonly record struct HotkeyCombination(uint Modifiers, uint VirtualKey);

    private readonly record struct NormalizedHotkey(
        HotkeyCombination Combination,
        string Modifiers,
        string Key);

    private sealed record OwnedRegistration(
        HotkeyRegistration Registration,
        HotkeyCombination Combination,
        Action Callback);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotkeysService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _hwnd = new WindowInteropHelper(window).Handle;
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("The window handle is not available for hotkey registration.");

        _hwndSource = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("The window message source is not available for hotkey registration.");

        _hwndSource.AddHook(HwndHook);
        _hookAttached = true;
    }

    internal HotkeyValidationResult Validate(string? modifiers, string? key)
    {
        return TryNormalize(modifiers, key, out var normalized, out var error)
            ? new HotkeyValidationResult(true, normalized.Modifiers, normalized.Key, string.Empty)
            : new HotkeyValidationResult(false, null, null, error);
    }

    internal HotkeyRegistrationResult Register(
        string featureName,
        string? modifiers,
        string? key,
        Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (_disposed)
        {
            return RegistrationFailure(
                "The global hotkey service has already been disposed.",
                modifiers,
                key);
        }

        if (string.IsNullOrWhiteSpace(featureName))
            return RegistrationFailure("A hotkey feature name is required.", modifiers, key);

        if (!TryNormalize(modifiers, key, out var normalized, out var validationError))
            return RegistrationFailure(validationError, modifiers, key);

        if (_registrationIdsByCombination.TryGetValue(normalized.Combination, out var conflictingId)
            && _registrationsById.TryGetValue(conflictingId, out var conflictingRegistration))
        {
            return new HotkeyRegistrationResult(
                false,
                null,
                normalized.Modifiers,
                normalized.Key,
                true,
                conflictingRegistration.Registration.FeatureName,
                false,
                null,
                $"{featureName} hotkey {normalized.Modifiers}+{normalized.Key} conflicts with the active {conflictingRegistration.Registration.FeatureName} hotkey.");
        }

        var id = AllocateRegistrationId();
        if (!RegisterHotKey(
                _hwnd,
                id,
                normalized.Combination.Modifiers,
                normalized.Combination.VirtualKey))
        {
            var nativeError = Marshal.GetLastWin32Error();
            var description = DescribeNativeFailure(
                $"Windows rejected the {featureName} hotkey {normalized.Modifiers}+{normalized.Key}",
                nativeError);
            Debug.WriteLine(description);

            return new HotkeyRegistrationResult(
                false,
                null,
                normalized.Modifiers,
                normalized.Key,
                false,
                null,
                true,
                nativeError,
                description);
        }

        var registration = new HotkeyRegistration(
            id,
            featureName,
            normalized.Modifiers,
            normalized.Key);
        var ownedRegistration = new OwnedRegistration(
            registration,
            normalized.Combination,
            callback);

        _registrationsById.Add(id, ownedRegistration);
        _registrationIdsByCombination.Add(normalized.Combination, id);

        return new HotkeyRegistrationResult(
            true,
            registration,
            normalized.Modifiers,
            normalized.Key,
            false,
            null,
            false,
            null,
            string.Empty);
    }

    internal HotkeyReplacementResult Replace(
        string featureName,
        HotkeyRegistration? currentRegistration,
        string? modifiers,
        string? key,
        Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var ownedForFeature = _registrationsById.Values
            .Where(owned => string.Equals(
                owned.Registration.FeatureName,
                featureName,
                StringComparison.Ordinal))
            .Select(owned => owned.Registration)
            .OrderBy(registration => registration.Id)
            .ToList();

        if (ownedForFeature.Count > 1)
        {
            return new HotkeyReplacementResult(
                false,
                false,
                ownedForFeature[0],
                ownedForFeature[1],
                null,
                false,
                false,
                null,
                null,
                $"{featureName} has multiple active registrations. Restart Noted before attempting another replacement.");
        }

        if (currentRegistration is null && ownedForFeature.Count == 1)
        {
            return new HotkeyReplacementResult(
                false,
                false,
                ownedForFeature[0],
                null,
                null,
                false,
                false,
                null,
                null,
                $"The active {featureName} registration is not synchronized with the window state.");
        }

        OwnedRegistration? currentOwned = null;
        if (currentRegistration is not null)
        {
            if (!_registrationsById.TryGetValue(currentRegistration.Id, out currentOwned)
                || !string.Equals(
                    currentOwned.Registration.FeatureName,
                    featureName,
                    StringComparison.Ordinal))
            {
                return new HotkeyReplacementResult(
                    false,
                    false,
                    ownedForFeature.SingleOrDefault(),
                    null,
                    null,
                    false,
                    false,
                    null,
                    null,
                    $"The previous {featureName} registration is no longer owned by the hotkey service.");
            }
        }

        if (!TryNormalize(modifiers, key, out var normalized, out var validationError))
        {
            var failedRegistration = RegistrationFailure(validationError, modifiers, key);
            return ReplacementFailure(currentOwned?.Registration, failedRegistration);
        }

        if (currentOwned is not null && currentOwned.Combination == normalized.Combination)
        {
            return new HotkeyReplacementResult(
                true,
                true,
                currentOwned.Registration,
                null,
                null,
                false,
                false,
                null,
                null,
                string.Empty);
        }

        var newRegistrationResult = Register(
            featureName,
            normalized.Modifiers,
            normalized.Key,
            callback);
        if (!newRegistrationResult.Success || newRegistrationResult.Registration is null)
            return ReplacementFailure(currentOwned?.Registration, newRegistrationResult);

        var newRegistration = newRegistrationResult.Registration;
        if (currentOwned is null)
        {
            return new HotkeyReplacementResult(
                true,
                false,
                newRegistration,
                null,
                newRegistrationResult,
                false,
                false,
                null,
                null,
                string.Empty);
        }

        var previousUnregistration = Unregister(currentOwned.Registration.Id);
        if (previousUnregistration.Success)
        {
            return new HotkeyReplacementResult(
                true,
                false,
                newRegistration,
                null,
                newRegistrationResult,
                false,
                false,
                null,
                null,
                string.Empty);
        }

        var newRegistrationRollback = Unregister(newRegistration.Id);
        if (newRegistrationRollback.Success)
        {
            return new HotkeyReplacementResult(
                false,
                false,
                currentOwned.Registration,
                null,
                newRegistrationResult,
                true,
                true,
                previousUnregistration.NativeError,
                null,
                $"The new {featureName} hotkey was registered, but the previous hotkey could not be released. The new registration was rolled back and the previous registration remains active. {previousUnregistration.Description}");
        }

        return new HotkeyReplacementResult(
            false,
            false,
            currentOwned.Registration,
            newRegistration,
            newRegistrationResult,
            true,
            false,
            previousUnregistration.NativeError,
            newRegistrationRollback.NativeError,
            $"The previous {featureName} hotkey and new hotkey could not be released. Both {currentOwned.Registration.Combination} and {newRegistration.Combination} may remain active until Noted exits. Previous release: {previousUnregistration.Description} New registration rollback: {newRegistrationRollback.Description}");
    }

    internal HotkeyUnregistrationResult Unregister(int id)
    {
        if (!_registrationsById.TryGetValue(id, out var ownedRegistration))
        {
            return new HotkeyUnregistrationResult(
                true,
                true,
                null,
                null,
                "The registration is already absent.");
        }

        if (_disposed)
        {
            return new HotkeyUnregistrationResult(
                false,
                false,
                ownedRegistration.Registration,
                null,
                "The global hotkey service has already been disposed.");
        }

        try
        {
            if (!UnregisterHotKey(_hwnd, id))
            {
                var nativeError = Marshal.GetLastWin32Error();
                var description = DescribeNativeFailure(
                    $"Windows could not unregister {ownedRegistration.Registration.FeatureName} hotkey {ownedRegistration.Registration.Combination}",
                    nativeError);
                Debug.WriteLine(description);

                return new HotkeyUnregistrationResult(
                    false,
                    false,
                    ownedRegistration.Registration,
                    nativeError,
                    description);
            }
        }
        catch (Exception exception)
        {
            var description = $"Unregistering {ownedRegistration.Registration.FeatureName} hotkey {ownedRegistration.Registration.Combination} threw {exception.GetType().Name}: {exception.Message}";
            Debug.WriteLine(description);
            return new HotkeyUnregistrationResult(
                false,
                false,
                ownedRegistration.Registration,
                null,
                description);
        }

        _registrationsById.Remove(id);
        if (_registrationIdsByCombination.TryGetValue(
                ownedRegistration.Combination,
                out var combinationId)
            && combinationId == id)
        {
            _registrationIdsByCombination.Remove(ownedRegistration.Combination);
        }

        return new HotkeyUnregistrationResult(
            true,
            false,
            ownedRegistration.Registration,
            null,
            string.Empty);
    }

    internal IReadOnlyList<HotkeyUnregistrationResult> UnregisterAll()
    {
        if (_disposed)
            return Array.Empty<HotkeyUnregistrationResult>();

        var results = new List<HotkeyUnregistrationResult>();
        foreach (var id in _registrationsById.Keys.ToList())
            results.Add(Unregister(id));

        return results;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        var cleanupResults = UnregisterAll();
        foreach (var failure in cleanupResults.Where(result => !result.Success))
            Debug.WriteLine($"Hotkey cleanup failure: {failure.Description}");

        if (_hookAttached)
        {
            try
            {
                _hwndSource.RemoveHook(HwndHook);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Removing the global hotkey window hook failed: {exception}");
            }

            _hookAttached = false;
        }

        _disposed = true;
    }

    private IntPtr HwndHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WM_HOTKEY
            && _registrationsById.TryGetValue(wParam.ToInt32(), out var registration))
        {
            registration.Callback();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private int AllocateRegistrationId()
    {
        while (_registrationsById.ContainsKey(_nextRegistrationId))
            _nextRegistrationId++;

        return _nextRegistrationId++;
    }

    private static HotkeyRegistrationResult RegistrationFailure(
        string description,
        string? requestedModifiers,
        string? requestedKey)
    {
        return new HotkeyRegistrationResult(
            false,
            null,
            requestedModifiers,
            requestedKey,
            false,
            null,
            false,
            null,
            description);
    }

    private static HotkeyReplacementResult ReplacementFailure(
        HotkeyRegistration? activeRegistration,
        HotkeyRegistrationResult newRegistrationResult)
    {
        return new HotkeyReplacementResult(
            false,
            false,
            activeRegistration,
            null,
            newRegistrationResult,
            false,
            false,
            null,
            null,
            newRegistrationResult.Description);
    }

    private static string DescribeNativeFailure(string context, int nativeError)
    {
        return $"{context}. Win32 error {nativeError}: {new Win32Exception(nativeError).Message}";
    }

    private static bool TryNormalize(
        string? modifiersText,
        string? keyText,
        out NormalizedHotkey normalized,
        out string error)
    {
        normalized = default;

        if (string.IsNullOrWhiteSpace(modifiersText))
        {
            error = "Select at least one modifier.";
            return false;
        }

        var modifierParts = modifiersText.Split('+');
        if (modifierParts.Length == 0 || modifierParts.Any(string.IsNullOrWhiteSpace))
        {
            error = $"The modifier combination '{modifiersText}' is malformed.";
            return false;
        }

        var modifierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint modifierFlags = 0;
        foreach (var rawPart in modifierParts)
        {
            var part = rawPart.Trim();
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => (Name: "Ctrl", Flag: MOD_CONTROL),
                "alt" => (Name: "Alt", Flag: MOD_ALT),
                "shift" => (Name: "Shift", Flag: MOD_SHIFT),
                "win" or "windows" => (Name: "Win", Flag: MOD_WIN),
                _ => (Name: string.Empty, Flag: 0u)
            };

            if (modifier.Flag == 0)
            {
                error = $"Unknown hotkey modifier '{part}'.";
                return false;
            }

            if (!modifierNames.Add(modifier.Name))
            {
                error = $"The hotkey modifier '{modifier.Name}' is duplicated.";
                return false;
            }

            modifierFlags |= modifier.Flag;
        }

        if (modifierFlags == 0)
        {
            error = "Select at least one modifier.";
            return false;
        }

        if (!TryNormalizeKey(keyText, out var normalizedKey, out var virtualKey, out error))
            return false;

        var orderedModifiers = new List<string>(4);
        if ((modifierFlags & MOD_CONTROL) != 0) orderedModifiers.Add("Ctrl");
        if ((modifierFlags & MOD_ALT) != 0) orderedModifiers.Add("Alt");
        if ((modifierFlags & MOD_SHIFT) != 0) orderedModifiers.Add("Shift");
        if ((modifierFlags & MOD_WIN) != 0) orderedModifiers.Add("Win");

        normalized = new NormalizedHotkey(
            new HotkeyCombination(modifierFlags, virtualKey),
            string.Join("+", orderedModifiers),
            normalizedKey);
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeKey(
        string? keyText,
        out string normalizedKey,
        out uint virtualKey,
        out string error)
    {
        normalizedKey = string.Empty;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(keyText))
        {
            error = "Select a hotkey key.";
            return false;
        }

        var key = keyText.Trim().ToUpperInvariant();
        switch (key)
        {
            case "SPACE": normalizedKey = "Space"; virtualKey = 0x20; break;
            case "TAB": normalizedKey = "Tab"; virtualKey = 0x09; break;
            case "ENTER":
            case "RETURN": normalizedKey = "Enter"; virtualKey = 0x0D; break;
            case "ESC":
            case "ESCAPE": normalizedKey = "Escape"; virtualKey = 0x1B; break;
            case "DELETE":
            case "DEL": normalizedKey = "Delete"; virtualKey = 0x2E; break;
            case "INSERT":
            case "INS": normalizedKey = "Insert"; virtualKey = 0x2D; break;
            case "HOME": normalizedKey = "Home"; virtualKey = 0x24; break;
            case "END": normalizedKey = "End"; virtualKey = 0x23; break;
            case "PAGEUP":
            case "PRIOR": normalizedKey = "PageUp"; virtualKey = 0x21; break;
            case "PAGEDOWN":
            case "NEXT": normalizedKey = "PageDown"; virtualKey = 0x22; break;
            case "LEFT": normalizedKey = "Left"; virtualKey = 0x25; break;
            case "RIGHT": normalizedKey = "Right"; virtualKey = 0x27; break;
            case "UP": normalizedKey = "Up"; virtualKey = 0x26; break;
            case "DOWN": normalizedKey = "Down"; virtualKey = 0x28; break;
            case "PRINT": normalizedKey = "Print"; virtualKey = 0x2C; break;
            case "PAUSE": normalizedKey = "Pause"; virtualKey = 0x13; break;
            default:
                if (key.Length == 1 && key[0] is >= 'A' and <= 'Z')
                {
                    normalizedKey = key;
                    virtualKey = key[0];
                }
                else if (key.Length == 1 && key[0] is >= '0' and <= '9')
                {
                    normalizedKey = key;
                    virtualKey = key[0];
                }
                else if (key.Length is 2 or 3
                    && key[0] == 'F'
                    && int.TryParse(key[1..], out var functionKey)
                    && functionKey is >= 1 and <= 12)
                {
                    normalizedKey = $"F{functionKey}";
                    virtualKey = (uint)(0x70 + functionKey - 1);
                }
                else
                {
                    error = $"Unknown hotkey key '{keyText.Trim()}'.";
                    return false;
                }

                break;
        }

        error = string.Empty;
        return true;
    }
}
