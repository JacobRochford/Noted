using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public sealed class ScratchpadWindowViewModel : INotifyPropertyChanged
{
    private readonly IAppSettingsService _settingsService;
    private readonly IScratchpadContentService _contentService;
    private ScratchpadWindowState _windowState;
    private bool _ghostModeEnabled;
    private bool _wordWrapEnabled;
    private double _fontSize;
    private int _wordCount;
    private int _charCount;
    private int _lineCount;
    private string _findText = string.Empty;
    private string _replaceText = string.Empty;
    private bool _isFindBarVisible;
    private string _findResultText = string.Empty;
    private string? _contentPersistenceError;
    private string? _settingsPersistenceError;

    public ScratchpadWindowState InitialWindowState => _windowState;
    public string InitialContent { get; }
    public bool InitialContentLoadSucceeded { get; }

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set
        {
            if (_ghostModeEnabled == value) return;
            _ghostModeEnabled = value;
            OnPropertyChanged();
            PersistWindowState();
        }
    }

    public bool WordWrapEnabled
    {
        get => _wordWrapEnabled;
        set
        {
            if (_wordWrapEnabled == value) return;
            _wordWrapEnabled = value;
            OnPropertyChanged();
            PersistWindowState();
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 0.01) return;
            _fontSize = value;
            OnPropertyChanged();
            PersistWindowState();
        }
    }

    public int WordCount { get => _wordCount; set { _wordCount = value; OnPropertyChanged(); } }
    public int CharCount { get => _charCount; set { _charCount = value; OnPropertyChanged(); } }
    public int LineCount { get => _lineCount; set { _lineCount = value; OnPropertyChanged(); } }
    public string FindText { get => _findText; set { _findText = value; OnPropertyChanged(); } }
    public string ReplaceText { get => _replaceText; set { _replaceText = value; OnPropertyChanged(); } }
    public bool IsFindBarVisible { get => _isFindBarVisible; set { _isFindBarVisible = value; OnPropertyChanged(); } }
    public string FindResultText { get => _findResultText; set { _findResultText = value; OnPropertyChanged(); } }

    public string? PersistenceError => _contentPersistenceError ?? _settingsPersistenceError;

    public bool HasPersistenceError => !string.IsNullOrWhiteSpace(PersistenceError);

    public ScratchpadWindowViewModel(
        IAppSettingsService settingsService,
        IScratchpadContentService contentService)
    {
        _settingsService = settingsService;
        _contentService = contentService;
        _windowState = LoadWindowState();
        _ghostModeEnabled = _windowState.GhostModeEnabled;
        _wordWrapEnabled = _windowState.WordWrapEnabled;
        _fontSize = _windowState.FontSize;

        var loadResult = _contentService.TryLoadContent();
        InitialContentLoadSucceeded = loadResult.Success;
        InitialContent = loadResult.Success && loadResult.Exists
            ? loadResult.Content ?? string.Empty
            : string.Empty;

        if (!loadResult.Success)
            SetContentPersistenceError($"Scratchpad content could not be loaded: {loadResult.Error}");
        else if (!string.IsNullOrWhiteSpace(loadResult.Warning))
            SetContentPersistenceError(loadResult.Warning);
    }

    public void ApplyNormalizedWindowState(ScratchpadWindowState state)
    {
        _windowState = state;
        _ghostModeEnabled = state.GhostModeEnabled;
        _wordWrapEnabled = state.WordWrapEnabled;
        _fontSize = state.FontSize;
    }

    public void UpdateCounts(string plainText)
    {
        var normalizedText = plainText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalizedText.EndsWith('\n'))
            normalizedText = normalizedText[..^1];

        CharCount = normalizedText.Length;
        LineCount = normalizedText.Length == 0
            ? 0
            : normalizedText.Count(character => character == '\n') + 1;
        WordCount = string.IsNullOrWhiteSpace(normalizedText)
            ? 0
            : normalizedText.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public bool TrySaveWindowLayout(
        double left,
        double top,
        double width,
        double height,
        bool? reopenOnStartup = null)
    {
        _windowState = _windowState with
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            ReopenOnStartup = reopenOnStartup ?? _windowState.ReopenOnStartup
        };
        return TrySaveWindowState();
    }

    public bool TrySaveContent(string rtfContent)
    {
        var result = _contentService.TrySaveContent(rtfContent);
        if (!result.Success)
        {
            SetContentPersistenceError($"Scratchpad content could not be saved: {result.Error}");
            return false;
        }

        SetContentPersistenceError(result.Warning);
        return true;
    }

    public void ReportPersistenceError(string message)
    {
        SetContentPersistenceError(message);
    }

    private ScratchpadWindowState LoadWindowState()
    {
        try
        {
            return _settingsService.LoadScratchpadWindowState();
        }
        catch (IOException ex)
        {
            SetSettingsPersistenceError($"Scratchpad settings could not be loaded: {ex.Message}");
            return new ScratchpadWindowState();
        }
        catch (UnauthorizedAccessException ex)
        {
            SetSettingsPersistenceError($"Scratchpad settings could not be loaded: {ex.Message}");
            return new ScratchpadWindowState();
        }
        catch (SecurityException ex)
        {
            SetSettingsPersistenceError($"Scratchpad settings could not be loaded: {ex.Message}");
            return new ScratchpadWindowState();
        }
    }

    private void PersistWindowState()
    {
        _windowState = _windowState with
        {
            GhostModeEnabled = _ghostModeEnabled,
            WordWrapEnabled = _wordWrapEnabled,
            FontSize = _fontSize
        };
        TrySaveWindowState();
    }

    private bool TrySaveWindowState()
    {
        try
        {
            _settingsService.SaveScratchpadWindowState(_windowState);
            SetSettingsPersistenceError(null);
            return true;
        }
        catch (IOException ex)
        {
            SetSettingsPersistenceError($"Scratchpad settings could not be saved: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SetSettingsPersistenceError($"Scratchpad settings could not be saved: {ex.Message}");
        }
        catch (SecurityException ex)
        {
            SetSettingsPersistenceError($"Scratchpad settings could not be saved: {ex.Message}");
        }

        return false;
    }

    private void SetContentPersistenceError(string? error)
    {
        if (_contentPersistenceError == error)
            return;

        _contentPersistenceError = error;
        OnPropertyChanged(nameof(PersistenceError));
        OnPropertyChanged(nameof(HasPersistenceError));
    }

    private void SetSettingsPersistenceError(string? error)
    {
        if (_settingsPersistenceError == error)
            return;

        _settingsPersistenceError = error;
        OnPropertyChanged(nameof(PersistenceError));
        OnPropertyChanged(nameof(HasPersistenceError));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
