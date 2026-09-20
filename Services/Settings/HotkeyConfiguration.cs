using Noted.Helpers;

namespace Noted.Services;

internal enum HotkeyFeature { Notes, Checklist, Dictionary }
internal enum HotkeyChangeStatus { Unavailable, InvalidSelection, Invalid, Ambiguous, Failed, Unchanged, Saved, SaveFailed }
internal sealed record HotkeyChangeResult(HotkeyChangeStatus Status, string Description);

internal interface IHotkeyRegistrationService : IDisposable
{
    HotkeyValidationResult Validate(string? modifiers, string? key);
    HotkeyRegistrationResult Register(string featureName, string modifiers, string key, Action callback);
    HotkeyReplacementResult Replace(string featureName, HotkeyRegistration? currentRegistration, string modifiers, string key, Action callback);
}

internal sealed class HotkeyConfiguration : IDisposable
{
    private readonly IAppSettingsService _settingsService;
    private readonly Func<IHotkeyRegistrationService> _createService;
    private readonly Func<HotkeyFeature, Action> _getAction;
    private bool _cleanupCompleted;
    private bool _hotkeysInitialized;
    private bool _hotkeyInitializationInProgress;
    private IHotkeyRegistrationService? _globalHotkeysService;
    private HotkeyRegistration? _notesHotkeyRegistration;
    private HotkeyRegistration? _additionalNotesHotkeyRegistration;
    private HotkeyRegistration? _checklistHotkeyRegistration;
    private HotkeyRegistration? _additionalChecklistHotkeyRegistration;
    private HotkeyRegistration? _dictionaryHotkeyRegistration;
    private HotkeyRegistration? _additionalDictionaryHotkeyRegistration;
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

            if (_notesHotkeyRegistration is null)
            {
                var (modifiers, key) = _settingsService.LoadNotesHotkey();
                var result = RegisterWithFallback(
                    "Notes",
                    modifiers,
                    key,
                    "Ctrl+Shift",
                    "Space",
                    _getAction(HotkeyFeature.Notes),
                    _settingsService.SaveNotesHotkey);
                _notesHotkeyRegistration = result.Registration;
                if (result.Warning is not null)
                    warnings.Add(result.Warning);
            }

            if (_checklistHotkeyRegistration is null)
            {
                var (modifiers, key) = _settingsService.LoadChecklistHotkey();
                var result = RegisterWithFallback(
                    "Checklist",
                    modifiers,
                    key,
                    "Alt",
                    "C",
                    _getAction(HotkeyFeature.Checklist),
                    _settingsService.SaveChecklistHotkey);
                _checklistHotkeyRegistration = result.Registration;
                if (result.Warning is not null)
                    warnings.Add(result.Warning);
            }

            if (_dictionaryHotkeyRegistration is null)
            {
                var (modifiers, key) = _settingsService.LoadDictionaryHotkey();
                var result = RegisterWithFallback(
                    "Dictionary",
                    modifiers,
                    key,
                    "Alt",
                    "D",
                    _getAction(HotkeyFeature.Dictionary),
                    _settingsService.SaveDictionaryHotkey);
                _dictionaryHotkeyRegistration = result.Registration;
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
        Action<string, string> saveHotkey)
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

            string? persistenceWarning = null;
            try
            {
                saveHotkey(registration.Modifiers, registration.Key);
            }
            catch (SettingsPersistenceException exception)
            {
                ExceptionDiagnostics.Record(exception);
                persistenceWarning =
                    $" The fallback is active for this session, but saving it failed: {exception.Message}";
            }

            return new InitialHotkeyResult(
                registration,
                $"{featureName} hotkey {requestedDisplay} was unavailable. " +
                $"Active fallback: {registration.Combination}.{persistenceWarning}");
        }

        var failureSummary = string.Join(
            " ",
            failures.Where(failure => !string.IsNullOrWhiteSpace(failure)).Distinct());
        return new InitialHotkeyResult(
            null,
            $"{featureName} hotkey {requestedModifiers}+{requestedKey} could not be registered. " +
            $"No fallback is active. {failureSummary}".TrimEnd());
    }
    internal HotkeyRegistration? GetHotkeyRegistration(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => _notesHotkeyRegistration,
        HotkeyFeature.Checklist => _checklistHotkeyRegistration,
        HotkeyFeature.Dictionary => _dictionaryHotkeyRegistration,
        _ => null
    };

    internal HotkeyRegistration? GetAdditionalHotkeyRegistration(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => _additionalNotesHotkeyRegistration,
        HotkeyFeature.Checklist => _additionalChecklistHotkeyRegistration,
        HotkeyFeature.Dictionary => _additionalDictionaryHotkeyRegistration,
        _ => null
    };

    private void SetHotkeyRegistrations(
        HotkeyFeature feature,
        HotkeyRegistration? registration,
        HotkeyRegistration? additionalRegistration)
    {
        switch (feature)
        {
            case HotkeyFeature.Notes:
                _notesHotkeyRegistration = registration;
                _additionalNotesHotkeyRegistration = additionalRegistration;
                break;
            case HotkeyFeature.Checklist:
                _checklistHotkeyRegistration = registration;
                _additionalChecklistHotkeyRegistration = additionalRegistration;
                break;
            case HotkeyFeature.Dictionary:
                _dictionaryHotkeyRegistration = registration;
                _additionalDictionaryHotkeyRegistration = additionalRegistration;
                break;
        }
    }

    internal (string Modifiers, string Key) LoadHotkey(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => _settingsService.LoadNotesHotkey(),
        HotkeyFeature.Checklist => _settingsService.LoadChecklistHotkey(),
        HotkeyFeature.Dictionary => _settingsService.LoadDictionaryHotkey(),
        _ => GetDefaultHotkey(feature)
    };

    private void SaveHotkey(HotkeyFeature feature, string modifiers, string key)
    {
        switch (feature)
        {
            case HotkeyFeature.Notes:
                _settingsService.SaveNotesHotkey(modifiers, key);
                break;
            case HotkeyFeature.Checklist:
                _settingsService.SaveChecklistHotkey(modifiers, key);
                break;
            case HotkeyFeature.Dictionary:
                _settingsService.SaveDictionaryHotkey(modifiers, key);
                break;
        }
    }

    internal static (string Modifiers, string Key) GetDefaultHotkey(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => ("Ctrl+Shift", "Space"),
        HotkeyFeature.Checklist => ("Alt", "C"),
        HotkeyFeature.Dictionary => ("Alt", "D"),
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null)
    };

    internal static string GetHotkeyFeatureName(HotkeyFeature feature) => feature switch
    {
        HotkeyFeature.Notes => "Notes",
        HotkeyFeature.Checklist => "Checklist",
        HotkeyFeature.Dictionary => "Dictionary",
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null)
    };


    internal HotkeyChangeResult Change(HotkeyFeature feature, IReadOnlyList<string> modifiers, string? key)
    {
        if (modifiers.Count == 0 || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrEmpty(HotkeyConstants.BuildHotkey(modifiers.ToList(), key)))
            return new(HotkeyChangeStatus.InvalidSelection, "Please select at least one modifier and a key.");
        if (_globalHotkeysService is null)
            return new(HotkeyChangeStatus.Unavailable,
                "The global hotkey service is unavailable. Restart Noted and try again.");
        var validation = _globalHotkeysService.Validate(string.Join("+", modifiers), key);
        if (!validation.Success || validation.Modifiers is null || validation.Key is null)
            return new(HotkeyChangeStatus.Invalid, validation.Description);
        var featureName = GetHotkeyFeatureName(feature);
        if (GetAdditionalHotkeyRegistration(feature) is not null)
            return new(HotkeyChangeStatus.Ambiguous,
                $"Multiple {featureName} hotkeys may still be active after an earlier rollback failure. " +
                "Restart Noted before attempting another replacement.");
        var replacement = _globalHotkeysService.Replace(featureName, GetHotkeyRegistration(feature),
            validation.Modifiers, validation.Key, _getAction(feature));
        SetHotkeyRegistrations(feature, replacement.ActiveRegistration, replacement.AdditionalActiveRegistration);
        if (!replacement.Success)
            return new(HotkeyChangeStatus.Failed, replacement.Description);
        if (replacement.NoChange || replacement.ActiveRegistration is null)
            return new(HotkeyChangeStatus.Unchanged, string.Empty);
        try
        {
            SaveHotkey(feature, replacement.ActiveRegistration.Modifiers, replacement.ActiveRegistration.Key);
        }
        catch (SettingsPersistenceException exception)
        {
            ExceptionDiagnostics.Record(exception);
            return new(HotkeyChangeStatus.SaveFailed,
                $"The hotkey is active as {replacement.ActiveRegistration.Combination}, " +
                $"but the setting could not be saved: {exception.Message}");
        }
        return new(HotkeyChangeStatus.Saved,
            $"{featureName} hotkey updated to: {replacement.ActiveRegistration.Combination}");
    }

    public void Dispose()
    {
        if (_cleanupCompleted) return;
        _cleanupCompleted = true;
        _globalHotkeysService?.Dispose();
        _globalHotkeysService = null;
        foreach (var feature in Enum.GetValues<HotkeyFeature>())
            SetHotkeyRegistrations(feature, null, null);
        _hotkeysInitialized = false;
    }
}
