using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;

namespace Noted.ViewModels;

public enum ChecklistSortMode  { Manual, Priority, DueDate, Alphabetical }

public sealed class ChecklistWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan s_saveQuietPeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan s_saveMaximumDelay = TimeSpan.FromSeconds(2);

    private readonly IChecklistContentService _contentService;
    private readonly SaveScheduler<IReadOnlyList<ChecklistItemState>> _saveScheduler;
    private readonly Action<Action> _uiThreadInvoke;
    private readonly ChecklistTabs _checklistTabs;
    private bool _ghostModeEnabled;
    private string _searchQuery = "";
    private ChecklistSortMode _sortMode = ChecklistSortMode.Manual;
    private bool _isSearchVisible;
    private bool _isRefreshing;
    private bool _isBulkUpdating;
    private bool _isUpdatingItemText;
    private bool _deferContentRefresh;
    private readonly Dictionary<ChecklistItem, ChecklistTab?> _unfinishedEdits = [];
    private string? _persistenceError;
    private string? _itemSaveError;
    private string? _tabSaveError;
    private string? _itemSaveWarning;
    private string? _tabSaveWarning;
    private string? _loadNotice;

    internal event EventHandler? ViewRefreshing;
    internal event EventHandler? ViewRefreshed;
    internal event EventHandler? ReminderStateChanged;

    // Source collection in saved order
    public ObservableCollection<ChecklistItem> Items { get; } = new();

    // Filtered + sorted view shown to the ListBox
    public ObservableCollection<ChecklistItem> FilteredItems { get; } = new();
    public ObservableCollection<ChecklistTab> Tabs => _checklistTabs.All;
    public ObservableCollection<ChecklistTab> VisibleTabs => _checklistTabs.Visible;

    // Window state

    public bool GhostModeEnabled
    {
        get => _ghostModeEnabled;
        set { if (_ghostModeEnabled != value) { _ghostModeEnabled = value; OnPropertyChanged(); } }
    }

    // Filter / search / sort

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value) return;
            _searchQuery = value;
            OnPropertyChanged();
            RefreshFilteredItems(force: true);
        }
    }

    public ChecklistTab? SelectedTab
    {
        get => _checklistTabs.Selected;
        private set => _checklistTabs.Select(value);
    }

    public bool HasSelectedTab => SelectedTab is not null;

    public bool CanAddItem => SelectedTab is not null &&
        (SelectedTab.Kind != ChecklistTabKind.Done ||
         VisibleTabs.Any(tab => tab.Kind != ChecklistTabKind.Done));

    public IEnumerable<ChecklistTab> CustomTabs =>
        _checklistTabs.Custom;

    public ChecklistSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode == value) return;
            _sortMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanManuallyReorder));
            RefreshFilteredItems(force: true);
        }
    }

    public bool CanManuallyReorder => SortMode == ChecklistSortMode.Manual;

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        set { if (_isSearchVisible != value) { _isSearchVisible = value; OnPropertyChanged(); } }
    }

    public string? PersistenceError
    {
        get => _persistenceError;
        private set
        {
            if (_persistenceError == value) return;
            _persistenceError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPersistenceError));
        }
    }

    public bool HasPersistenceError => !string.IsNullOrWhiteSpace(PersistenceError);
    internal bool RecoveryIssuesFoundThisRun { get; }

    // Progress / stats

    public int TotalCount     => Items.Count;
    public int CompletedCount => Items.Count(i => i.IsChecked);
    public int RemainingCount => Items.Count(i => !i.IsChecked);
    public double ProgressPercent => TotalCount > 0 ? (double)CompletedCount / TotalCount * 100 : 0;
    public string ProgressText => TotalCount == 0 ? "" : $"{CompletedCount}/{TotalCount}";
    public bool HasCompleted  => Items.Any(i => i.IsChecked);

    public string FooterText => RemainingCount switch
    {
        _ when TotalCount == 0 => "No items — click + Add",
        0 => "All done! 🎉",
        1 => "1 item remaining",
        _ => $"{RemainingCount} items remaining"
    };

    // Constructor

    public ChecklistWindowViewModel(
        IChecklistContentService contentService,
        Dispatcher dispatcher,
        Action<Action> uiThreadInvoke,
        bool ghostModeEnabled)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(uiThreadInvoke);
        _contentService = contentService;
        _saveScheduler = new SaveScheduler<IReadOnlyList<ChecklistItemState>>(
            dispatcher,
            s_saveQuietPeriod,
            s_saveMaximumDelay,
            SaveItemsSnapshot);
        _uiThreadInvoke = uiThreadInvoke;
        _ghostModeEnabled = ghostModeEnabled;

        var loadResult = _contentService.LoadItems();
        RecoveryIssuesFoundThisRun = loadResult.Issues.Count > 0;
        SetLoadIssues(loadResult.Issues);
        _checklistTabs = new ChecklistTabs(loadResult.Tabs, TrySaveTabsSnapshot);
        _checklistTabs.SelectionChanged += ChecklistTabs_SelectionChanged;
        var customTabsById = CustomTabs.ToDictionary(tab => tab.Id, StringComparer.OrdinalIgnoreCase);
        var reservedIds = loadResult.Items.Select(item => item.Id).Where(id => id != Guid.Empty).ToHashSet();
        var seenIds = new HashSet<Guid>();
        var repairedItems = false;
        foreach (var d in loadResult.Items)
        {
            var id = d.Id;
            var duplicate = id != Guid.Empty && !seenIds.Add(id);
            if (id == Guid.Empty || duplicate)
            {
                do
                {
                    id = Guid.NewGuid();
                }
                while (!reservedIds.Add(id));
                repairedItems = true;
            }
            customTabsById.TryGetValue(d.TabId ?? "", out var assignedTab);
            var item = new ChecklistItem
            {
                Id        = id,
                Text      = d.Text ?? "",
                IsChecked = d.IsChecked,
                Priority  = d.Priority,
                TabId     = assignedTab?.Id,
                DueDate   = d.DueDate,
                DueTime   = d.DueTime,
                NotificationStyle = d.NotificationStyle,
                NotificationOptions = d.NotificationOptions,
                ReminderAtUtc = duplicate ? null : d.ReminderAtUtc,
                ReminderAtDueTime = !duplicate && d.ReminderAtDueTime,
                ReminderHandledAtUtc = duplicate ? null : d.ReminderHandledAtUtc,
                Notes     = d.Notes ?? "",
                CreatedAt = d.CreatedAt == default ? DateTime.Now : d.CreatedAt
            };
            if (!duplicate && item.ReminderAtUtc.HasValue)
            {
                if (d.ReminderOccurrence is { IsValid: true } occurrence && occurrence.ScheduledAtUtc == item.ReminderAtUtc)
                    item.ReminderOccurrence = occurrence with
                    {
                        Options = occurrence.Options?.Normalize(), ScheduledAtUtc = occurrence.ScheduledAtUtc.ToUniversalTime(),
                        AcknowledgedAtUtc = occurrence.AcknowledgedAtUtc?.ToUniversalTime(),
                        SilencedAtUtc = occurrence.SilencedAtUtc?.ToUniversalTime()
                    };
                else if (item.NotificationStyle == ChecklistNotificationStyle.Urgent)
                    item.ReminderOccurrence = ChecklistReminderOccurrence.Create(item.ReminderAtUtc.Value);
            }
            repairedItems |= item.NotificationStyle != d.NotificationStyle || item.ReminderOccurrence != d.ReminderOccurrence;
            repairedItems |= item.ReminderAtUtc != d.ReminderAtUtc ||
                             item.ReminderHandledAtUtc != d.ReminderHandledAtUtc;
            item.PropertyChanged += Item_PropertyChanged;
            Items.Add(item);
        }

        Items.CollectionChanged += Items_CollectionChanged;
        RefreshFilteredItems();
        if (repairedItems) SaveItems();
    }

    internal void ResetPresentation(bool ghostModeEnabled, string? selectedTabId)
    {
        ClearItemEdits();
        foreach (var item in Items)
        {
            item.IsExpanded = false;
            item.IsEditingTitle = false;
        }
        SearchQuery = "";
        SortMode = ChecklistSortMode.Manual;
        IsSearchVisible = false;
        GhostModeEnabled = ghostModeEnabled;
        var selected = selectedTabId is null ? null : FindTab(selectedTabId);
        SelectTab(selected?.IsVisible == true ? selected : VisibleTabs.FirstOrDefault());
        RefreshFilteredItems();
    }

    // Tabs

    public void SelectTab(ChecklistTab? tab) => _checklistTabs.Select(tab);

    public ChecklistTab? FindTab(string id) =>
        _checklistTabs.Find(id);

    internal bool RevealItem(Guid id, out ChecklistItem? item)
    {
        item = Items.FirstOrDefault(candidate => candidate.Id == id && !candidate.IsChecked);
        if (item is null) return false;
        var tab = item.TabId is { } tabId ? FindTab(tabId) : FindTab(ChecklistTab.AllId);
        if (tab is null) return false;
        if (!tab.IsVisible && !SetDefaultTabVisibility(tab.Id, true)) return false;
        SearchQuery = "";
        SelectTab(tab);
        return FilteredItems.Contains(item);
    }

    public bool SetDefaultTabVisibility(string id, bool isVisible)
    {
        var saved = _checklistTabs.SetDefaultVisibility(id, isVisible);
        if (saved)
            OnPropertyChanged(nameof(CanAddItem));
        return saved;
    }

    public bool TryCreateTab(string name, out ChecklistTab? createdTab)
    {
        createdTab = null;
        return _checklistTabs.TryCreate(name, out createdTab);
    }

    public bool TryRenameTab(ChecklistTab tab, string name) =>
        _checklistTabs.TryRename(tab, name);

    public bool TryDeleteTab(ChecklistTab tab)
    {
        if (!_checklistTabs.TryDelete(tab))
            return false;

        RunBulkUpdate(() =>
        {
            foreach (var item in Items.Where(item => item.TabId == tab.Id))
                item.TabId = null;
        });
        OnPropertyChanged(nameof(CanAddItem));
        return true;
    }

    public bool IsTabNameAvailable(string name, ChecklistTab? existingTab) =>
        _checklistTabs.IsNameAvailable(name, existingTab);

    public void MoveItemToTab(ChecklistItem item, string? tabId)
    {
        if (!Items.Contains(item)) return;
        var validTabId = tabId is not null &&
                         CustomTabs.Any(tab => tab.Id == tabId)
            ? tabId
            : null;
        item.TabId = validTabId;
    }

    private void ChecklistTabs_SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(HasSelectedTab));
        OnPropertyChanged(nameof(CanAddItem));
        RefreshFilteredItems(force: true);
    }

    // Collection / item change handlers

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ChecklistItem item in e.NewItems)
                item.PropertyChanged += Item_PropertyChanged;

        if (e.OldItems != null)
            foreach (ChecklistItem item in e.OldItems)
            {
                item.PropertyChanged -= Item_PropertyChanged;
                if (e.Action != NotifyCollectionChangedAction.Move)
                {
                    _unfinishedEdits.Remove(item);
                    item.CancelReminder();
                }
            }

        if (e.Action == NotifyCollectionChangedAction.Reset) _unfinishedEdits.Clear();
        if (_isBulkUpdating) return;

        NotifyProgress();
        SaveItems();
        RefreshFilteredItems();
        ReminderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRefreshing ||
            _isBulkUpdating ||
            e.PropertyName is nameof(ChecklistItem.IsExpanded) or nameof(ChecklistItem.IsEditingTitle) or nameof(ChecklistItem.HasActiveReminder) or nameof(ChecklistItem.DueAtLocal)) return;

        NotifyProgress();
        SaveItems();
        if (!_isUpdatingItemText)
            RefreshFilteredItems();
        if (e.PropertyName is nameof(ChecklistItem.ReminderAtUtc) or
            nameof(ChecklistItem.ReminderHandledAtUtc) or nameof(ChecklistItem.ReminderOccurrence) or
            nameof(ChecklistItem.NotificationStyle) or nameof(ChecklistItem.IsChecked))
            ReminderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void DeferContentRefresh(bool defer, bool refresh = true)
    {
        if (_deferContentRefresh == defer) return;
        _deferContentRefresh = defer;
        if (!defer && refresh) RefreshFilteredItems();
    }

    internal bool IsItemEditing(ChecklistItem item) => _unfinishedEdits.ContainsKey(item);

    internal void StartItemEdit(ChecklistItem item) => _unfinishedEdits.TryAdd(item, SelectedTab);

    internal void RestoreItemEdit(ChecklistItem item, string? originTabId) =>
        _unfinishedEdits.TryAdd(item, originTabId is null ? null : FindTab(originTabId));

    internal string? GetEditOriginTabId(ChecklistItem item) => _unfinishedEdits.GetValueOrDefault(item)?.Id;

    internal void EndItemEdit(ChecklistItem item) => _unfinishedEdits.Remove(item);

    internal void ClearItemEdits()
    {
        _unfinishedEdits.Clear();
        _deferContentRefresh = false;
    }

    internal void UpdateItemText(ChecklistItem item, string text)
    {
        if (item.Text == text) return;

        // schedule typing without moving the row under the caret.
        _isUpdatingItemText = true;
        try
        {
            item.Text = text;
        }
        finally
        {
            _isUpdatingItemText = false;
        }
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(RemainingCount));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(FooterText));
    }

    // Filtering / sorting

    public void RefreshFilteredItems(bool force = false)
    {
        if (_deferContentRefresh && !force) return;
        ViewRefreshing?.Invoke(this, EventArgs.Empty);
         _isRefreshing = true;
        try {
            var source = Items.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchQuery))
                source = source.Where(i =>
                    IsItemEditing(i) || i.Text.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                    i.Notes.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

            source = SelectedTab?.Kind switch
            {
                ChecklistTabKind.All => source,
                ChecklistTabKind.Urgent => source.Where(
                    item => item.Priority == ChecklistPriority.High || IsEditingInSelectedTab(item)),
                ChecklistTabKind.Done => source.Where(item => item.IsChecked || IsEditingInSelectedTab(item)),
                ChecklistTabKind.Custom => source.Where(
                    item => item.TabId == SelectedTab.Id || IsEditingInSelectedTab(item)),
                _ => Enumerable.Empty<ChecklistItem>()
            };

            source = SortMode switch
            {
                ChecklistSortMode.Priority    => source.OrderByDescending(i => (int)i.Priority),
                ChecklistSortMode.DueDate     => source.OrderBy(i => i.DueDate.HasValue ? 0 : 1)
                                                    .ThenBy(i => i.DueAtLocal ?? DateTime.MaxValue),
                ChecklistSortMode.Alphabetical => source.OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase),
                _                              => source
            };

            var result = source.ToList();

            // keep existing row containers when sorting an unfinished edit.
            var visible = result.ToHashSet();
            for (int i = FilteredItems.Count - 1; i >= 0; i--)
                if (!visible.Contains(FilteredItems[i])) FilteredItems.RemoveAt(i);

            for (int i = 0; i < result.Count; i++)
            {
                if (i < FilteredItems.Count && ReferenceEquals(FilteredItems[i], result[i])) continue;
                var oldIndex = FilteredItems.IndexOf(result[i]);
                if (oldIndex < 0) FilteredItems.Insert(i, result[i]);
                else FilteredItems.Move(oldIndex, i);
            }
        } finally { _isRefreshing = false; }
        ViewRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsEditingInSelectedTab(ChecklistItem item) =>
        _unfinishedEdits.TryGetValue(item, out var origin) && ReferenceEquals(SelectedTab, origin);

    // CRUD

    public ChecklistItem? AddItem(ChecklistItem? anchor = null)
    {
        ChecklistItem? item = null;
        _uiThreadInvoke(() =>
        {
            var creationTab = GetCreationTab();
            if (creationTab is null) return;

            PrepareViewForNewItem(creationTab);
            item = new ChecklistItem
            {
                CreatedAt = DateTime.Now,
                Priority = creationTab.Kind == ChecklistTabKind.Urgent
                    ? ChecklistPriority.High
                    : ChecklistPriority.None,
                TabId = creationTab.Kind == ChecklistTabKind.Custom
                    ? creationTab.Id
                    : null
            };

            var anchorIndex = SortMode == ChecklistSortMode.Manual && anchor is not null
                ? Items.IndexOf(anchor)
                : -1;
            if (anchorIndex >= 0)
                Items.Insert(anchorIndex + 1, item!);
            else
                Items.Add(item!);
        });
        return item;
    }

    public ChecklistItem? InsertItemAfter(ChecklistItem anchor)
    {
        return AddItem(anchor);
    }

    private ChecklistTab? GetCreationTab()
    {
        if (SelectedTab?.Kind != ChecklistTabKind.Done)
            return SelectedTab;

        return VisibleTabs.FirstOrDefault(tab => tab.Kind == ChecklistTabKind.All) ??
               VisibleTabs.FirstOrDefault(tab => tab.Kind == ChecklistTabKind.Custom) ??
               VisibleTabs.FirstOrDefault(tab => tab.Kind == ChecklistTabKind.Urgent);
    }

    private void PrepareViewForNewItem(ChecklistTab creationTab)
    {
        var refreshNeeded = false;
        if (!ReferenceEquals(SelectedTab, creationTab))
        {
            SelectedTab = creationTab;
            refreshNeeded = true;
        }

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            _searchQuery = "";
            OnPropertyChanged(nameof(SearchQuery));
            refreshNeeded = true;
        }

        if (refreshNeeded)
            RefreshFilteredItems(force: true);
    }

    public void RemoveItem(ChecklistItem item) => _uiThreadInvoke(() => Items.Remove(item));

    public void DuplicateItem(ChecklistItem item)
    {
        var idx = Items.IndexOf(item);
        var dup = new ChecklistItem
        {
            Text      = item.Text,
            Priority  = item.Priority,
            TabId     = item.TabId,
            DueDate   = item.DueDate,
            DueTime   = item.DueTime,
            Notes     = item.Notes,
            CreatedAt = DateTime.Now
        };
        Items.Insert(idx >= 0 ? idx + 1 : Items.Count, dup);
    }

    internal bool TrySetReminder(ChecklistItem item, DateTimeOffset? reminder, out string? error) =>
        TrySetReminder(item, reminder, item.NotificationStyle, item.NotificationOptions, out error);

    internal bool TrySetReminder(ChecklistItem item, DateTimeOffset? reminder, ChecklistNotificationStyle style,
        ChecklistNotificationOptions? options, out string? error)
    {
        if (!TryValidateReminderChange(item, reminder, style, out error)) return false;
        RunBulkUpdate(() => ApplyReminderPreferences(item, reminder, style, options));
        return TryFlushPendingItems(out error);
    }

    private bool TryValidateReminderChange(ChecklistItem item, DateTimeOffset? reminder, ChecklistNotificationStyle style, out string? error)
    {
        error = null;
        if (!Items.Contains(item) || item.IsChecked && reminder.HasValue)
        {
            error = "Choose an incomplete task to set a notification.";
            return false;
        }
        var rescheduled = reminder != item.ReminderAtUtc;
        if (rescheduled && reminder <= DateTimeOffset.UtcNow)
        {
            error = "Choose a notification time in the future, or turn the notification off.";
            return false;
        }
        if (!rescheduled && reminder.HasValue && style != item.NotificationStyle &&
            (item.ReminderHandledAtUtc.HasValue || item.ReminderOccurrence?.Options is not null))
        {
            error = "Notification delivery has started. Reschedule it or turn it off before changing its style.";
            return false;
        }
        return true;
    }

    private static void ApplyReminderPreferences(ChecklistItem item, DateTimeOffset? reminder, ChecklistNotificationStyle style,
        ChecklistNotificationOptions? options)
    {
        item.NotificationStyle = style;
        item.NotificationOptions = options;
        item.ReminderAtUtc = reminder;
    }

    internal bool TrySetSchedule(ChecklistItem item, DateTime? dueDate, TimeSpan? dueTime, DateTimeOffset? reminder,
        bool atDueTime, ChecklistNotificationStyle style, ChecklistNotificationOptions? options, out string? error)
    {
        error = null;
        if (!Items.Contains(item) || item.IsChecked || dueTime.HasValue && !dueDate.HasValue)
        { error = "Choose an incomplete task and a date for its time."; return false; }
        if (dueTime < TimeSpan.Zero || dueTime >= TimeSpan.FromDays(1))
        { error = "Choose a valid task time."; return false; }
        if (atDueTime)
        {
            if (dueDate is null || dueTime is null || reminder is null)
            { error = "A due-time notification needs a task date and time."; return false; }
            if (item.ReminderAtDueTime && dueDate == item.DueDate && dueTime == item.DueTime)
                reminder = item.ReminderAtUtc;
            else if (!Noted.Helpers.ChecklistReminderTime.TryGetUtc(dueDate, dueTime, DateTimeOffset.MinValue,
                TimeZoneInfo.Local, out reminder, out error)) return false;
        }
        if (!TryValidateReminderChange(item, reminder, style, out error)) return false;
        RunBulkUpdate(() =>
        {
            item.DueDate = dueDate;
            item.DueTime = dueTime;
            ApplyReminderPreferences(item, reminder, style, options);
            item.ReminderAtDueTime = atDueTime;
        });
        return TryFlushPendingItems(out error);
    }

    internal bool TryCaptureUrgentOptions(IReadOnlyList<ChecklistReminder> reminders, out string? error)
    {
        var items = Items.ToDictionary(item => item.Id);
        foreach (var reminder in reminders)
            if (reminder.Style == ChecklistNotificationStyle.Urgent && items.TryGetValue(reminder.TaskId, out var item) &&
                item.ReminderOccurrence is { Options: null } occurrence && occurrence.Id == reminder.Occurrence?.Id)
                item.ReminderOccurrence = occurrence with { Options = (reminder.Options ?? item.NotificationOptions ?? new()).Normalize() };
        // persist the snapshot before any new acknowledgment controls or sounds are presented.
        return TrySaveItems(null, out error);
    }

    internal bool TryAcknowledgeUrgentReminder(Guid taskId, Guid occurrenceId, DateTimeOffset now, out string? error) =>
        TryUpdateUrgentReminder(taskId, occurrenceId, now, acknowledge: true, out error);

    internal bool TrySilenceUrgentReminder(Guid taskId, Guid occurrenceId, DateTimeOffset now, out string? error) =>
        TryUpdateUrgentReminder(taskId, occurrenceId, now, acknowledge: false, out error);

    private bool TryUpdateUrgentReminder(Guid taskId, Guid occurrenceId, DateTimeOffset now, bool acknowledge, out string? error)
    {
        var item = Items.FirstOrDefault(item => item.Id == taskId);
        if (item is null || item.IsChecked || item.NotificationStyle != ChecklistNotificationStyle.Urgent ||
            item.ReminderAtUtc is not { } due || (due > now && item.ReminderHandledAtUtc is null) ||
            item.ReminderOccurrence is not { } occurrence || occurrence.Id != occurrenceId)
        {
            error = "This reminder occurrence is no longer available.";
            return false;
        }
        var updated = acknowledge ? occurrence with { Options = occurrence.Options?.Normalize(), ScheduledAtUtc = occurrence.ScheduledAtUtc.ToUniversalTime(), AcknowledgedAtUtc = occurrence.AcknowledgedAtUtc ?? now.ToUniversalTime() }
            : occurrence with { SilencedAtUtc = occurrence.SilencedAtUtc ?? now.ToUniversalTime() };
        item.ReminderOccurrence = updated;
        if (TrySaveItems(null, out error)) return true;
        // a failed acknowledgment must remain visible; do not overwrite a replacement occurrence.
        if (item.ReminderOccurrence == updated) item.ReminderOccurrence = occurrence;
        return false;
    }

    internal bool TryMarkRemindersHandled(IReadOnlyList<ChecklistReminder> reminders, DateTimeOffset handledAt, out string? error)
    {
        var itemsById = Items.ToDictionary(item => item.Id);
        RunBulkUpdate(() =>
        {
            foreach (var reminder in reminders)
            {
                if (itemsById.TryGetValue(reminder.TaskId, out var item) && !item.IsChecked &&
                    item.ReminderAtUtc == reminder.ReminderAtUtc && item.NotificationStyle == reminder.Style &&
                    item.ReminderHandledAtUtc is null &&
                    (reminder.Occurrence is null || item.ReminderOccurrence?.Id == reminder.Occurrence.Id))
                    item.ReminderHandledAtUtc = handledAt;
            }
        });
        return TryFlushPendingItems(out error);
    }

    // Bulk operations

    public void ClearCompleted()
    {
        _uiThreadInvoke(() =>
        {
            var completedItems = Items.Where(item => item.IsChecked).ToList();
            if (completedItems.Count == 0) return;

            RunBulkUpdate(() =>
            {
                foreach (var item in completedItems)
                    Items.Remove(item);
            });
        });
    }

    public void CheckAll() => SetAllChecked(true);
    public void UncheckAll() => SetAllChecked(false);

    private void SetAllChecked(bool isChecked)
    {
        _uiThreadInvoke(() =>
        {
            var changedItems = FilteredItems
                .Where(item => item.IsChecked != isChecked)
                .ToList();
            if (changedItems.Count == 0) return;

            RunBulkUpdate(() =>
            {
                foreach (var item in changedItems)
                    item.IsChecked = isChecked;
            });
        });
    }

    private void RunBulkUpdate(Action update)
    {
        _isBulkUpdating = true;
        try
        {
            update();
        }
        finally
        {
            _isBulkUpdating = false;
        }

        NotifyProgress();
        SaveItems();
        RefreshFilteredItems();
        ReminderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Reorder

    public void MoveItemUp(ChecklistItem item)
    {
        if (!CanManuallyReorder) return;
        var visibleIndex = FilteredItems.IndexOf(item);
        if (visibleIndex > 0)
            MoveItem(item, FilteredItems[visibleIndex - 1]);
    }

    public void MoveItemDown(ChecklistItem item)
    {
        if (!CanManuallyReorder) return;
        var visibleIndex = FilteredItems.IndexOf(item);
        if (visibleIndex >= 0 && visibleIndex < FilteredItems.Count - 1)
            MoveItem(item, FilteredItems[visibleIndex + 1]);
    }

    public void MoveToTop(ChecklistItem item)
    {
        if (!CanManuallyReorder || FilteredItems.Count == 0) return;
        var firstVisible = FilteredItems[0];
        if (!ReferenceEquals(item, firstVisible))
            MoveItem(item, firstVisible);
    }

    public void MoveToBottom(ChecklistItem item)
    {
        if (!CanManuallyReorder || FilteredItems.Count == 0) return;
        var lastVisible = FilteredItems[^1];
        if (!ReferenceEquals(item, lastVisible))
            MoveItem(item, lastVisible);
    }

    public void MoveItem(ChecklistItem source, ChecklistItem target)
    {
        if (!CanManuallyReorder) return;
        var si = Items.IndexOf(source);
        var ti = Items.IndexOf(target);
        if (si >= 0 && ti >= 0 && si != ti)
            Items.Move(si, ti);
    }

    public void MoveItem(ChecklistItem source, ChecklistItem target, bool placeAfter)
    {
        if (!CanManuallyReorder) return;
        var sourceIndex = Items.IndexOf(source);
        var targetIndex = Items.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;

        var newIndex = placeAfter
            ? sourceIndex < targetIndex ? targetIndex : targetIndex + 1
            : sourceIndex < targetIndex ? targetIndex - 1 : targetIndex;
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);

        if (newIndex != sourceIndex)
            Items.Move(sourceIndex, newIndex);
    }

    // Persistence

    public bool TryFlushPendingItems(out string? error)
    {
        return _saveScheduler.TryFlush(out error);
    }

    internal bool TrySaveItems(IReadOnlySet<ChecklistItem>? excludedItems, out string? error)
    {
        var snapshot = CreateItemsSnapshot(excludedItems);
        _saveScheduler.Schedule(snapshot);
        if (!_saveScheduler.TryFlush(out error))
        {
            // a retry outside the explicit completion must keep the still-open creations.
            if (excludedItems is { Count: > 0 }) SaveItems();
            return false;
        }

        // a notification during the write may have queued newer content.
        if (!snapshot.SequenceEqual(CreateItemsSnapshot(excludedItems)))
        {
            error = "Task content changed while saving. Finish the item again to save the latest changes.";
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        _checklistTabs.SelectionChanged -= ChecklistTabs_SelectionChanged;
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in Items)
            item.PropertyChanged -= Item_PropertyChanged;
        _saveScheduler.Dispose();
    }

    private void SaveItems()
    {
        _saveScheduler.Schedule(CreateItemsSnapshot());
    }

    private IReadOnlyList<ChecklistItemState> CreateItemsSnapshot(IReadOnlySet<ChecklistItem>? excludedItems = null)
    {
        return Items.Where(item => excludedItems?.Contains(item) != true).Select(i => new ChecklistItemState
        {
            Id        = i.Id,
            Text      = i.Text,
            IsChecked = i.IsChecked,
            Priority  = i.Priority,
            TabId     = i.TabId,
            DueDate   = i.DueDate,
            DueTime   = i.DueTime,
            ReminderAtUtc = i.ReminderAtUtc,
            ReminderAtDueTime = i.ReminderAtDueTime,
            ReminderHandledAtUtc = i.ReminderHandledAtUtc,
            NotificationStyle = i.NotificationStyle,
            NotificationOptions = i.NotificationOptions,
            ReminderOccurrence = i.ReminderOccurrence,
            Notes     = i.Notes,
            CreatedAt = i.CreatedAt
        }).ToList();
    }

    private bool TrySaveTabsSnapshot(IReadOnlyList<ChecklistTabState> tabs)
    {
        try
        {
            var saveResult = _contentService.SaveTabs(tabs);
            _tabSaveError = null;
            _tabSaveWarning = saveResult.Warning;
            _loadNotice = null;
            RefreshPersistenceError();
            return true;
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            _tabSaveError = "Task tabs could not be saved.";
            _tabSaveWarning = null;
            RefreshPersistenceError();
            return false;
        }
    }

    private PersistenceSaveResult SaveItemsSnapshot(IReadOnlyList<ChecklistItemState> items)
    {
        try
        {
            var saveResult = _contentService.SaveItems(items);
            _itemSaveError = null;
            _itemSaveWarning = saveResult.Warning;
            _loadNotice = null;
            RefreshPersistenceError();
            return PersistenceSaveResult.Succeeded();
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            ExceptionDiagnostics.Record(ex);
            _itemSaveError = "Task content could not be saved.";
            _itemSaveWarning = null;
            RefreshPersistenceError();
            return PersistenceSaveResult.Failed(_itemSaveError);
        }
    }

    private void SetLoadIssues(IReadOnlyList<ChecklistContentIssue> issues)
    {
        _loadNotice = issues.Count == 0
            ? null
            : string.Join(
                " ",
                issues.Select(issue => $"{Path.GetFileName(issue.FilePath)}: {issue.Message}"));
        RefreshPersistenceError();
    }

    // Failures precede warnings; item saves take precedence within each category.
    private void RefreshPersistenceError()
        => PersistenceError = _itemSaveError
            ?? _tabSaveError
            ?? _itemSaveWarning
            ?? _tabSaveWarning
            ?? _loadNotice;

    private static bool IsExpectedPersistenceException(Exception exception) =>
        FileSystemErrors.IsExpected(exception);

}
