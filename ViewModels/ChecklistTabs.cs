using System.Collections.ObjectModel;
using Noted.Models;

namespace Noted.ViewModels;

internal sealed class ChecklistTabs
{
    private readonly Func<IReadOnlyList<ChecklistTabState>, bool> _trySave;
    private int _nextCustomTabOrder = 100;

    internal ChecklistTabs(
        IReadOnlyList<ChecklistTabState> states,
        Func<IReadOnlyList<ChecklistTabState>, bool> trySave)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(trySave);
        _trySave = trySave;
        Initialize(states);
    }

    internal ObservableCollection<ChecklistTab> All { get; } = new();
    internal ObservableCollection<ChecklistTab> Visible { get; } = new();
    internal ChecklistTab? Selected { get; private set; }
    internal IEnumerable<ChecklistTab> Custom =>
        All.Where(tab => tab.Kind == ChecklistTabKind.Custom);

    internal event Action? SelectionChanged;

    internal void Select(ChecklistTab? tab)
    {
        if (tab is null || !tab.IsVisible || !All.Contains(tab))
            return;

        SetSelected(tab);
    }

    internal ChecklistTab? Find(string id) =>
        All.FirstOrDefault(tab => string.Equals(tab.Id, id, StringComparison.Ordinal));

    internal bool SetDefaultVisibility(string id, bool isVisible)
    {
        var tab = Find(id);
        if (tab is null || !tab.IsSystem || tab.IsVisible == isVisible)
            return tab is not null;

        tab.IsVisible = isVisible;
        if (!_trySave(CreateSnapshot()))
        {
            tab.IsVisible = !isVisible;
            return false;
        }

        RefreshVisible();
        return true;
    }

    internal bool TryCreate(string name, out ChecklistTab? createdTab)
    {
        createdTab = null;
        var normalizedName = NormalizeName(name);
        if (!IsNameAvailable(normalizedName, null))
            return false;

        var tab = new ChecklistTab(
            Guid.NewGuid().ToString("N"),
            normalizedName,
            ChecklistTabKind.Custom,
            isVisible: true,
            _nextCustomTabOrder++);
        All.Add(tab);
        if (!_trySave(CreateSnapshot()))
        {
            All.Remove(tab);
            _nextCustomTabOrder--;
            return false;
        }

        RefreshVisible();
        Select(tab);
        createdTab = tab;
        return true;
    }

    internal bool TryRename(ChecklistTab tab, string name)
    {
        if (!tab.CanDelete || !All.Contains(tab))
            return false;

        var normalizedName = NormalizeName(name);
        if (!IsNameAvailable(normalizedName, tab))
            return false;

        var previousName = tab.Name;
        tab.Name = normalizedName;
        if (_trySave(CreateSnapshot()))
            return true;

        tab.Name = previousName;
        return false;
    }

    internal bool TryDelete(ChecklistTab tab)
    {
        if (!tab.CanDelete || !All.Contains(tab))
            return false;

        var index = All.IndexOf(tab);
        All.Remove(tab);
        if (!_trySave(CreateSnapshot()))
        {
            All.Insert(index, tab);
            return false;
        }

        RefreshVisible();
        return true;
    }

    internal bool IsNameAvailable(string name, ChecklistTab? existingTab)
    {
        var normalizedName = NormalizeName(name);
        return !string.IsNullOrWhiteSpace(normalizedName) &&
               !All.Any(tab =>
                   !ReferenceEquals(tab, existingTab) &&
                   string.Equals(tab.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    internal IReadOnlyList<ChecklistTabState> CreateSnapshot() =>
        All.Select(tab => new ChecklistTabState
        {
            Id = tab.Id,
            Name = tab.Name,
            Kind = tab.Kind,
            IsVisible = tab.IsVisible,
            Order = tab.Order
        }).ToList();

    private void Initialize(IReadOnlyList<ChecklistTabState> states)
    {
        AddSystemTab(
            states.FirstOrDefault(state => state.Kind == ChecklistTabKind.All),
            ChecklistTab.AllId,
            "All",
            ChecklistTabKind.All,
            0);
        AddSystemTab(
            states.FirstOrDefault(state => state.Kind == ChecklistTabKind.Urgent),
            ChecklistTab.UrgentId,
            "Urgent",
            ChecklistTabKind.Urgent,
            1);

        var usedIds = new HashSet<string>(All.Select(tab => tab.Id), StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(All.Select(tab => tab.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var state in states
                     .Where(state => state.Kind == ChecklistTabKind.Custom)
                     .OrderBy(state => state.Order))
        {
            var id = state.Id?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!usedIds.Add(id));
            }

            var name = MakeUniqueName(NormalizeName(state.Name), usedNames);
            var order = Math.Max(100, state.Order);
            All.Add(new ChecklistTab(
                id,
                name,
                ChecklistTabKind.Custom,
                isVisible: true,
                order));
            _nextCustomTabOrder = Math.Max(_nextCustomTabOrder, order + 1);
        }

        AddSystemTab(
            states.FirstOrDefault(state => state.Kind == ChecklistTabKind.Done),
            ChecklistTab.DoneId,
            "Done",
            ChecklistTabKind.Done,
            int.MaxValue);

        foreach (var tab in All.Where(tab => tab.IsVisible).OrderBy(tab => tab.Order))
            Visible.Add(tab);
        SetSelected(Visible.FirstOrDefault(), notify: false);
    }

    private void AddSystemTab(
        ChecklistTabState? state,
        string id,
        string name,
        ChecklistTabKind kind,
        int order)
    {
        All.Add(new ChecklistTab(id, name, kind, state?.IsVisible ?? true, order));
    }

    private void RefreshVisible()
    {
        var previousSelection = Selected;
        Visible.Clear();
        foreach (var tab in All.Where(tab => tab.IsVisible).OrderBy(tab => tab.Order))
            Visible.Add(tab);

        SetSelected(previousSelection is not null && Visible.Contains(previousSelection)
            ? previousSelection
            : Visible.FirstOrDefault());
    }

    private void SetSelected(ChecklistTab? tab, bool notify = true)
    {
        if (ReferenceEquals(Selected, tab))
            return;

        if (Selected is not null)
            Selected.IsSelected = false;
        Selected = tab;
        if (Selected is not null)
            Selected.IsSelected = true;
        if (notify)
            SelectionChanged?.Invoke();
    }

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim() ?? "";
        return normalized.Length <= 30 ? normalized : normalized[..30].TrimEnd();
    }

    private static string MakeUniqueName(string name, ISet<string> usedNames)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Tab" : name;
        var nameToTry = baseName;
        var suffix = 2;
        while (!usedNames.Add(nameToTry))
        {
            var suffixText = $" ({suffix++})";
            var prefixLength = Math.Max(1, 30 - suffixText.Length);
            nameToTry = $"{baseName[..Math.Min(baseName.Length, prefixLength)].TrimEnd()}{suffixText}";
        }

        return nameToTry;
    }
}
