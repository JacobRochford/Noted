using System.Windows.Input;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

internal sealed class UtilityVisibilityViewModel : ObservableObject
{
    private readonly IAppSettingsService _settings;
    private readonly Func<Action, bool> _trySave;
    private HashSet<string> _hidden = new(StringComparer.Ordinal);

    public IReadOnlyList<UtilityOption> Utilities { get; }
    public IReadOnlyList<UtilityOption> VisibleUtilities => Utilities.Where(option => option.IsVisible).ToArray();
    public bool HasVisibleUtilities => Utilities.Any(option => option.IsVisible);

    internal UtilityVisibilityViewModel(IAppSettingsService settings, Func<Action, bool> trySave)
    {
        _settings = settings;
        _trySave = trySave;
        Utilities = UtilityCatalog.Utilities.Select(definition => new UtilityOption(definition, this)).ToArray();
        Reload();
    }

    internal void Reload()
    {
        _hidden = new HashSet<string>(_settings.LoadHiddenUtilities(), StringComparer.Ordinal);
        NotifyVisibility();
    }

    internal bool IsVisible(string id) => !_hidden.Contains(id);

    internal void SetVisible(string id, bool visible)
    {
        if (IsVisible(id) == visible) return;
        var hidden = new HashSet<string>(_hidden, StringComparer.Ordinal);
        if (visible) hidden.Remove(id);
        else hidden.Add(id);
        if (!_trySave(() => _settings.SaveHiddenUtilities(hidden.OrderBy(value => value, StringComparer.Ordinal).ToArray())))
        {
            NotifyVisibility();
            return;
        }
        _hidden = hidden;
        NotifyVisibility();
    }

    private void NotifyVisibility()
    {
        foreach (var option in Utilities) option.Refresh();
        OnPropertyChanged(nameof(VisibleUtilities));
        OnPropertyChanged(nameof(HasVisibleUtilities));
    }
}

internal sealed class UtilityOption : ObservableObject
{
    private readonly UtilityVisibilityViewModel _owner;
    public string Id { get; }
    public string DisplayName { get; }
    public string Icon { get; }
    public double IconFontSize { get; }
    public string IconFontWeight { get; }
    public string ToolTip => $"Show or hide {DisplayName}";
    public ICommand OpenCommand { get; }
    public bool IsVisible
    {
        get => _owner.IsVisible(Id);
        set => _owner.SetVisible(Id, value);
    }

    internal UtilityOption(UtilityDefinition definition, UtilityVisibilityViewModel owner)
    {
        _owner = owner;
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Icon = definition.Icon;
        IconFontSize = definition.IconFontSize;
        IconFontWeight = definition.IconFontWeight;
        OpenCommand = new UtilityCommand(definition.Toggle);
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsVisible));

    private sealed class UtilityCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
