namespace Noted.Models;

public enum ChecklistTabKind
{
    All,
    Urgent,
    Done,
    Custom
}

public sealed class ChecklistTab : ObservableObject
{
    public const string AllId = "all";
    public const string UrgentId = "urgent";
    public const string DoneId = "done";

    private string _name;
    private bool _isVisible;
    private bool _isSelected;

    public ChecklistTab(string id, string name, ChecklistTabKind kind, bool isVisible, int order)
    {
        Id = id;
        _name = name;
        Kind = kind;
        _isVisible = isVisible;
        Order = order;
    }

    public string Id { get; }
    public ChecklistTabKind Kind { get; }
    public int Order { get; }
    public bool IsSystem => Kind != ChecklistTabKind.Custom;
    public bool CanDelete => Kind == ChecklistTabKind.Custom;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

}
