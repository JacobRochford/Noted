using Noted.Helpers;

namespace Noted.Services;

internal enum HotkeyFeature { Notes, Checklist, Dictionary, MiniPad, NoteEditor, JotCapture, Scratchpad, Jots }
internal enum HotkeyChangeStatus { Unavailable, InvalidSelection, Invalid, Ambiguous, Failed, Unchanged, Saved, SaveFailed }
internal sealed record HotkeyChangeResult(HotkeyChangeStatus Status, string Description);

internal interface IHotkeyRegistrationService : IDisposable
{
    HotkeyValidationResult Validate(string? modifiers, string? key);
    HotkeyRegistrationResult Register(string featureName, string modifiers, string key, Action callback);
    HotkeyReplacementResult Replace(string featureName, HotkeyRegistration? currentRegistration, string modifiers, string key, Action callback);
    HotkeyUnregistrationResult Unregister(int id);
}

internal sealed class HotkeyConfiguration : IDisposable
{
    internal const int CurrentDefaultsVersion = 1;
    private readonly IAppSettingsService _settingsService;
    private readonly Func<IHotkeyRegistrationService> _createService;
    private readonly Func<HotkeyFeature, Action> _getAction;
    private bool _cleanupCompleted;
    private bool _hotkeysInitialized;
    private bool _hotkeyInitializationInProgress;
    private IHotkeyRegistrationService? _globalHotkeysService;
    private readonly Dictionary<HotkeyFeature, HotkeyRegistration> _registrations = new();
    private readonly Dictionary<HotkeyFeature, HotkeyRegistration> _additionalRegistrations = new();
    private readonly Dictionary<HotkeyFeature, (string Modifiers, string Key, bool Enabled)> _pendingPreferences = new();
    private readonly Dictionary<HotkeyFeature, bool> _enabled = new();
    internal bool CallbacksSuspended { get; set; }

    internal bool IsEnabled(HotkeyFeature feature) =>
        _enabled.TryGetValue(feature, out var enabled) ? enabled : _settingsService.LoadHotkeyEnabled(PersistenceKeys.GetHotkey(feature));

    private Action GetCallback(HotkeyFeature feature) => () =>
    {
        if (!_cleanupCompleted && !CallbacksSuspended && IsEnabled(feature))
            _getAction(feature)();
    };

    private static readonly string[] s_fallbackHotkeyModifiers =
        { "Alt+Shift", "Ctrl+Alt", "Win+Alt", "Ctrl+Shift" };

    private sealed record InitialHotkeyResult(
        HotkeyRegistration? Registration,
        string? Warning);


    internal HotkeyConfiguration(IAppSettingsService settingsService,
        Func<IHotkeyRegistrationService> createService, Func<HotkeyFeature, Action> getAction)
    {
        _settingsService = settingsService;
        _createService = createService;
        _getAction = getAction;
    }

    internal IReadOnlyList<string> Initialize()
    {
        if (_cleanupCompleted
            || _hotkeysInitialized
            || _hotkeyInitializationInProgress)
            return Array.Empty<string>();

        _hotkeyInitializationInProgress = true;
        var warnings = new List<string>();

        try
        {
            _globalHotkeysService ??= _createService();

            var configuredCombinations = new Dictionary<HotkeyFeature, string>();
            foreach (var feature in Enum.GetValues<HotkeyFeature>())
            {
                _enabled[feature] = _settingsService.LoadHotkeyEnabled(PersistenceKeys.GetHotkey(feature));
                var configured = LoadHotkey(feature);
                if (_enabled[feature] && _globalHotkeysService.Validate(configured.Modifiers, configured.Key).Combination is { } combination)
                    configuredCombinations[feature] = combination;
            }
            foreach (var feature in Enum.GetValues<HotkeyFeature>())
            {
                var enabled = _enabled[feature];
                if (!enabled || GetHotkeyRegistration(feature) is not null)
                    continue;

                var (modifiers, key) = LoadHotkey(feature);
                var defaults = GetDefaultHotkey(feature);
                var result = RegisterWithFallback(GetHotkeyFeatureName(feature), modifiers, key,
                    defaults.Modifiers, defaults.Key, GetCallback(feature),
                    configuredCombinations.Where(pair => pair.Key != feature).Select(pair => pair.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
                SetHotkeyRegistrations(feature, result.Registration, null);
                if (result.Warning is not null)
                    warnings.Add(result.Warning);
            }

            _hotkeysInitialized = true;
        }
        catch (SettingsPersistenceException exception)
        {
            ExceptionDiagnostics.Record(exception);
            warnings.Add(
                $"Hotkey initialization stopped before all features were processed: {exception.Message}\n" +
                "Registrations already owned by Noted were retained; initialization may be retried without replacing the service.");
        }
        finally
        {
            _hotkeyInitializationInProgress = false;
        }

        return warnings;
    }

    private InitialHotkeyResult RegisterWithFallback(
        string featureName,
        string requestedModifiers,
        string requestedKey,
        string defaultFallbackModifiers,
        string defaultFallbackKey,
        Action callback,
        IReadOnlySet<string> otherConfiguredCombinations)
    {
        if (_globalHotkeysService is null)
        {
            return new InitialHotkeyResult(
                null,
                $"{featureName} hotkey could not be registered because the hotkey service is unavailable.");
        }

        var hotkeyOptions = new List<(string Modifiers, string Key)>
        {
            (requestedModifiers, requestedKey),
            (defaultFallbackModifiers, defaultFallbackKey),
            (s_fallbackHotkeyModifiers[0], requestedKey),
            (s_fallbackHotkeyModifiers[1], requestedKey),
            (s_fallbackHotkeyModifiers[2], requestedKey),
            (s_fallbackHotkeyModifiers[3], requestedKey),
            (s_fallbackHotkeyModifiers[0], defaultFallbackKey),
            (s_fallbackHotkeyModifiers[1], defaultFallbackKey),
            (s_fallbackHotkeyModifiers[2], defaultFallbackKey),
            (s_fallbackHotkeyModifiers[3], defaultFallbackKey)
        };

        var attemptedCombinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var requestedValidation = _globalHotkeysService.Validate(
            requestedModifiers,
            requestedKey);
        var requestedDisplay = requestedValidation.Combination
            ?? $"{requestedModifiers}+{requestedKey}";

        for (var index = 0; index < hotkeyOptions.Count; index++)
        {
            var option = hotkeyOptions[index];
            var validation = _globalHotkeysService.Validate(option.Modifiers, option.Key);
            if (!validation.Success
                || validation.Modifiers is null
                || validation.Key is null
                || validation.Combination is null)
            {
                failures.Add(validation.Description);
                continue;
            }

            // automatic fallbacks must not take editor shortcuts; explicit choices still apply.
            if (index > 0 && (otherConfiguredCombinations.Contains(validation.Combination) ||
                validation.Combination is "Ctrl+Shift+B" or "Ctrl+Shift+E" or "Ctrl+Shift+K" or
                    "Alt+Shift+Up" or "Alt+Shift+Down"))
                continue;
            if (!attemptedCombinations.Add(validation.Combination))
                continue;

            var registrationResult = _globalHotkeysService.Register(
                featureName,
                validation.Modifiers,
                validation.Key,
                callback);
            if (!registrationResult.Success || registrationResult.Registration is null)
            {
                failures.Add(registrationResult.Description);
                continue;
            }

            var registration = registrationResult.Registration;
            if (index == 0)
                return new InitialHotkeyResult(registration, null);

            return new InitialHotkeyResult(
                registration,
                $"{featureName} hotkey {requestedDisplay} was unavailable. " +
                $"Active fallback for this session: {registration.Combination}. Your configured shortcut was retained.");
        }

        var failureSummary = string.Join(
            " ",
            failures.Where(failure => !string.IsNullOrWhiteSpace(failure)).Distinct());
        return new InitialHotkeyResult(
            null,
            $"{featureName} hotkey {requestedModifiers}+{requestedKey} could not be registered. " +
            $"No fallback is active. {failureSummary}".TrimEnd());
    }

    internal HotkeyRegistration? GetHotkeyRegistration(HotkeyFeature feature) => _registrations.GetValueOrDefault(feature);
    internal HotkeyRegistration? GetAdditionalHotkeyRegistration(HotkeyFeature feature) => _additionalRegistrations.GetValueOrDefault(feature);

    private void SetHotkeyRegistrations(HotkeyFeature feature, HotkeyRegistration? registration,
        HotkeyRegistration? additionalRegistration)
    {
        if (registration is null) _registrations.Remove(feature);
        else _registrations[feature] = registration;
        if (additionalRegistration is null) _additionalRegistrations.Remove(feature);
        else _additionalRegistrations[feature] = additionalRegistration;
    }

    internal (string Modifiers, string Key) LoadHotkey(HotkeyFeature feature) =>
        _pendingPreferences.TryGetValue(feature, out var pending)
            ? (pending.Modifiers, pending.Key) : LoadSavedHotkey(feature);

    private (string Modifiers, string Key) LoadSavedHotkey(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => _settingsService.LoadNotesHotkey(),
        HotkeyFeature.Checklist => _settingsService.LoadChecklistHotkey(),
        HotkeyFeature.Dictionary => _settingsService.LoadDictionaryHotkey(),
        HotkeyFeature.MiniPad => _settingsService.LoadMiniPadHotkey(),
        HotkeyFeature.NoteEditor => _settingsService.LoadNoteEditorHotkey(),
        HotkeyFeature.JotCapture => _settingsService.LoadJotCaptureHotkey(),
        HotkeyFeature.Scratchpad => _settingsService.LoadScratchpadHotkey(),
        HotkeyFeature.Jots => _settingsService.LoadJotsHotkey(),
        _ => GetDefaultHotkey(feature)
    };

    internal static (string Modifiers, string Key) GetDefaultHotkey(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => ("Alt", "N"),
        HotkeyFeature.Checklist => ("Alt", "C"),
        HotkeyFeature.Dictionary => ("Alt", "D"),
        HotkeyFeature.MiniPad => ("Alt", "M"),
        HotkeyFeature.NoteEditor => ("Alt", "E"),
        HotkeyFeature.JotCapture => ("Alt", "J"),
        HotkeyFeature.Scratchpad => ("Alt", "S"),
        HotkeyFeature.Jots => ("Alt+Shift", "J"),
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null)
    };

    internal static string GetHotkeyFeatureName(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => "Noted",
        HotkeyFeature.Checklist => "Checklist",
        HotkeyFeature.Dictionary => "Dictionary",
        HotkeyFeature.MiniPad => "MiniPad",
        HotkeyFeature.NoteEditor => "Note Editor",
        HotkeyFeature.JotCapture => "Quick Jot",
        HotkeyFeature.Scratchpad => "Scratchpad",
        HotkeyFeature.Jots => "Jots collection",
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null)
    };


    internal HotkeyChangeResult Change(HotkeyFeature feature, IReadOnlyList<string> modifiers, string? key,
        bool enabled = true)
    {
        if (enabled && string.IsNullOrEmpty(HotkeyConstants.BuildHotkey(modifiers.ToList(), key)))
            return new(HotkeyChangeStatus.InvalidSelection, "Please select at least one modifier and a key.");
        if (_globalHotkeysService is null || _cleanupCompleted || CallbacksSuspended)
            return new(HotkeyChangeStatus.Unavailable, "The global hotkey service is unavailable.");
        var featureName = GetHotkeyFeatureName(feature);
        if (enabled && GetAdditionalHotkeyRegistration(feature) is not null)
            return new(HotkeyChangeStatus.Ambiguous,
                $"Multiple {featureName} hotkeys may still be active after an earlier rollback failure. " +
                "Restart Noted before attempting another replacement.");

        // keep the chosen shortcut when disabling it, even if saving it failed.
        var configured = LoadHotkey(feature);
        var desiredModifiers = configured.Modifiers;
        var desiredKey = configured.Key;
        var current = GetHotkeyRegistration(feature);
        if (enabled)
        {
            var validation = _globalHotkeysService.Validate(string.Join("+", modifiers), key);
            if (!validation.Success || validation.Modifiers is null || validation.Key is null)
                return new(HotkeyChangeStatus.Invalid, validation.Description);
            desiredModifiers = validation.Modifiers;
            desiredKey = validation.Key;
            if (current is null || current.Modifiers != desiredModifiers || current.Key != desiredKey)
            {
                var replacement = _globalHotkeysService.Replace(featureName, current,
                    desiredModifiers, desiredKey, GetCallback(feature));
                SetHotkeyRegistrations(feature, replacement.ActiveRegistration, replacement.AdditionalActiveRegistration);
                if (!replacement.Success)
                    return new(HotkeyChangeStatus.Failed, replacement.Description);
                if (replacement.ActiveRegistration is null)
                    return new(HotkeyChangeStatus.Failed, "No active registration was returned.");
            }
        }
        else
        {
            var remaining = new List<HotkeyRegistration>();
            var failures = new List<string>();
            foreach (var registration in new[] { current, GetAdditionalHotkeyRegistration(feature) })
            {
                if (registration is null) continue;
                var result = _globalHotkeysService.Unregister(registration.Id);
                if (!result.Success)
                {
                    remaining.Add(registration);
                    failures.Add(result.Description);
                }
            }
            SetHotkeyRegistrations(feature, remaining.FirstOrDefault(), remaining.Skip(1).FirstOrDefault());
            if (remaining.Count > 0)
                return new(HotkeyChangeStatus.Failed, string.Join(" ", failures));
        }

        _enabled[feature] = enabled;
        if (!_pendingPreferences.ContainsKey(feature)
            && LoadSavedHotkey(feature) == (desiredModifiers, desiredKey)
            && _settingsService.LoadHotkeyEnabled(PersistenceKeys.GetHotkey(feature)) == enabled)
            return new(HotkeyChangeStatus.Unchanged, string.Empty);

        _pendingPreferences[feature] = (desiredModifiers, desiredKey, enabled);
        try
        {
            _settingsService.SaveGlobalHotkey(PersistenceKeys.GetHotkey(feature), desiredModifiers, desiredKey, enabled);
            _pendingPreferences.Remove(feature);
        }
        catch (SettingsPersistenceException exception)
        {
            ExceptionDiagnostics.Record(exception);
            return new(HotkeyChangeStatus.SaveFailed,
                $"The shortcut is {(enabled ? "active" : "disabled")} for this session, " +
                $"but the preference could not be saved: {exception.Message} Apply again to retry.");
        }
        return new(HotkeyChangeStatus.Saved,
            $"{featureName} hotkey {(enabled ? $"updated to: {desiredModifiers}+{desiredKey}" : "disabled")}");
    }

    public void Dispose()
    {
        if (_cleanupCompleted) return;
        _cleanupCompleted = true;
        _globalHotkeysService?.Dispose();
        _globalHotkeysService = null;
        foreach (var feature in Enum.GetValues<HotkeyFeature>())
            SetHotkeyRegistrations(feature, null, null);
        _pendingPreferences.Clear();
        _hotkeysInitialized = false;
    }
}
