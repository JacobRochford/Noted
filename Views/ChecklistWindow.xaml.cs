using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class ChecklistWindow : OverlayWindow
{
    private readonly IAppSettingsService _settingsService;
    private readonly ChecklistWindowViewModel _viewModel;
    private readonly bool _ownsViewModel;
    private Point _dragStartPoint;
    private ChecklistItem? _pendingDragItem;
    private ListBoxItem? _pendingDragContainer;
    private ChecklistItem? _draggedItem;
    private ListBoxItem? _draggedContainer;
    private ChecklistTab? _tabBeingRenamed;
    private ChecklistPriorityColors _priorityColors = new();
    private string? _lastPersistenceWarning;
    private bool _reopenOnStartup;
    private bool _preserveOpenStateOnClose;
    private readonly Dictionary<ChecklistItem, (ChecklistPriority Priority, string? TabId)> _creations = [];
    private readonly ItemEditorSelections<ChecklistItem> _editorSelections = new();
    private readonly UtilityEditingSession<ChecklistItem> _editingSession;
    private ChecklistItem? _commandItem;
    private UtilityEditorField _sessionEditor;
    private UtilityItemSession? _pendingSessionFocus;
    private bool _completingItem;
    private FocusTarget? _lastEditorFocus;
    private FocusTarget? _refreshFocus;
    private ChecklistItem? _headerClickItem;
    private bool _headerClickEditsTitle;
    private bool _ignoreHeaderClick;
    private bool _isClosed;
    private readonly System.Windows.Threading.DispatcherTimer _dueStatusTimer = new(System.Windows.Threading.DispatcherPriority.Background)
    { Interval = TimeSpan.FromMinutes(1) };
    private ChecklistReminderActivation? _pendingReminderActivation;
    private ContextMenu? _reminderTaskMenu;
    private int _interactionVersion;

    private sealed record FocusTarget(
        FrameworkElement Element, ChecklistItem? Item, string Name, int Index, int Start, int Length);

    public ChecklistWindow(
        IAppSettingsService settingsService,
        IChecklistContentService contentService)
        : this(settingsService, null, contentService)
    {
    }

    internal ChecklistWindow(IAppSettingsService settingsService, ChecklistWindowViewModel viewModel)
        : this(settingsService, viewModel, null)
    {
    }

    private ChecklistWindow(
        IAppSettingsService settingsService,
        ChecklistWindowViewModel? viewModel,
        IChecklistContentService? contentService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        var state = _settingsService.LoadChecklistWindowState();
        _priorityColors = ChecklistColors.Normalize(state.PriorityColors);
        ApplyPriorityColors();
        _reopenOnStartup = state.ReopenOnStartup;
        RestoreWindowBounds(state.Left, state.Top, state.Width, state.Height);
        if (state.IsMaximized)
            WindowState = WindowState.Maximized;

        _ownsViewModel = viewModel is null;
        _viewModel = viewModel ?? new ChecklistWindowViewModel(
            contentService!,
            Dispatcher,
            action => Dispatcher.Invoke(action),
            state.GhostModeEnabled);
        _viewModel.ResetPresentation(state.GhostModeEnabled, state.SelectedTabId);
        _editingSession = new(this, settingsService.AppDataDirectory, PersistencePaths.ChecklistSessionFile,
            _viewModel.Items, CaptureEditingSession, () => TryFlushPendingContent(out _), EditorField,
            nameof(ChecklistItem.IsExpanded), nameof(ChecklistItem.IsEditingTitle), nameof(ChecklistItem.Text),
            nameof(ChecklistItem.Notes));
        RestoreEditingSession();
        DataContext = _viewModel;
        InitializeOverlay(state.GhostModeEnabled, state.GhostModeOpacity, state.Opacity);
        _viewModel.ViewRefreshing += ViewModel_ViewRefreshing;
        _viewModel.ViewRefreshed += ViewModel_ViewRefreshed;
        _viewModel.Items.CollectionChanged += Items_CollectionChanged;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        IsVisibleChanged += ChecklistWindow_IsVisibleChanged;
        _dueStatusTimer.Tick += RefreshDueIndicators;
        Microsoft.Win32.SystemEvents.TimeChanged += DueClockChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += DueClockChanged;
        Activated += RefreshDueIndicators;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChecklistWindowViewModel.TotalCount)) UpdateDueStatusTimer();
        if (e.PropertyName == nameof(ChecklistWindowViewModel.GhostModeEnabled))
        {
            ApplyGhostMode(_viewModel.GhostModeEnabled);
            SaveWindowState();
        }
        else if (e.PropertyName == nameof(ChecklistWindowViewModel.SelectedTab))
        {
            try
            {
                SaveWindowState();
            }
            catch (SettingsPersistenceException ex)
            {
                ExceptionDiagnostics.Record(ex);
                ShowPersistenceWarningOnce(ex.Message);
            }
        }
        else if (e.PropertyName == nameof(ChecklistWindowViewModel.PersistenceError) &&
                 !_viewModel.HasPersistenceError)
        {
            _lastPersistenceWarning = null;
        }
    }

    internal bool TryFlushPendingContent(out string? error)
    {
        var success = _viewModel.TryFlushPendingItems(out error);
        if (success && !_viewModel.HasPersistenceError)
            _lastPersistenceWarning = null;
        return success;
    }

    internal string? BackupBlockingIssue => _viewModel.PersistenceError;
    internal bool RecoveryBlocksBackup => _viewModel.RecoveryIssuesFoundThisRun;

    internal void OpenReminder(ChecklistReminderActivation activation)
    {
        _pendingReminderActivation = activation;
        TryOpenPendingReminder();
    }

    private void TryOpenPendingReminder()
    {
        if (_isClosed || _scheduleDialog is not null || _pendingReminderActivation is not { } activation || _completingItem) return;
        _pendingReminderActivation = null;
        if (activation.TaskId is { } id)
        {
            NavigateToReminder(id);
            return;
        }
        var tasks = _viewModel.Items.Where(item => !item.IsChecked &&
            item.ReminderHandledAtUtc == activation.BatchUtc).ToArray();
        if (tasks.Length == 0)
        {
            AppDialog.Show("These reminders are no longer available.", "Task notifications", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var menu = new ContextMenu
        {
            PlacementTarget = ItemsList,
            Placement = PlacementMode.Relative,
            MaxHeight = 360,
            Style = (Style)FindResource("NotedContextMenu")
        };
        if (_reminderTaskMenu is not null) _reminderTaskMenu.IsOpen = false;
        _reminderTaskMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_reminderTaskMenu, menu)) _reminderTaskMenu = null; };
        foreach (var task in tasks)
        {
            var taskId = task.Id;
            var entry = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(task.Text) ? "Untitled task" : task.Text,
                Style = (Style)FindResource("NotedContextMenuItem")
            };
            entry.Click += (_, _) => OpenReminder(new ChecklistReminderActivation(taskId, null));
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    internal bool NavigateToReminder(Guid id)
    {
        if (_scheduleDialog is not null)
        {
            _pendingReminderActivation = new(id, null);
            return false;
        }
        _interactionVersion++;
        if (!_viewModel.RevealItem(id, out var item))
        {
            AppDialog.Show("This task is completed or no longer available.", "Task notification", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        Activate();
        FocusRow(item!);
        return true;
    }

    private void ReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is not { IsChecked: false } item) return;
        EditReminder(item);
    }

    private void ReminderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenu(sender as MenuItem) is { IsChecked: false } item)
            EditReminder(item);
    }

    private ChecklistReminderDialog? _scheduleDialog;

    private void EditReminder(ChecklistItem item)
    {
        var dialog = new ChecklistReminderDialog(item.ReminderAtUtc, item.DueDate, item.NotificationStyle, item.NotificationOptions,
            _settingsService.LoadChecklistNotificationOptions(), item.DueTime, item.ReminderAtDueTime) { Owner = this };
        _scheduleDialog = dialog;
        bool? accepted;
        try { accepted = dialog.ShowDialog(); }
        finally
        {
            _scheduleDialog = null;
            if (_pendingReminderActivation is not null) Dispatcher.BeginInvoke(TryOpenPendingReminder);
        }
        if (accepted != true) return;
        if (!_viewModel.TrySetSchedule(item, dialog.ScheduledDueDate, dialog.ScheduledDueTime, dialog.ReminderAtUtc,
            dialog.ReminderAtDueTime, dialog.NotificationStyle, dialog.NotificationOptions, out var error))
            ShowPersistenceWarningOnce(error);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!TryFlushPendingContent(out var error))
        {
            e.Cancel = true;
            ShowPersistenceWarningOnce(error);
            return;
        }

        if (!_preserveOpenStateOnClose)
            _reopenOnStartup = false;

        _editingSession.Checkpoint();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _editingSession.Dispose();
        if (_reminderTaskMenu is not null) _reminderTaskMenu.IsOpen = false;
        _pendingReminderActivation = null;
        foreach (var item in _viewModel.Items.Where(_viewModel.IsItemEditing))
            item.IsEditingTitle = false;
        _viewModel.ClearItemEdits();
        _creations.Clear();
        _commandItem = null;
        _dueStatusTimer.Stop();
        _dueStatusTimer.Tick -= RefreshDueIndicators;
        Microsoft.Win32.SystemEvents.TimeChanged -= DueClockChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= DueClockChanged;
        Activated -= RefreshDueIndicators;
        _viewModel.ViewRefreshing -= ViewModel_ViewRefreshing;
        _viewModel.ViewRefreshed -= ViewModel_ViewRefreshed;
        _viewModel.Items.CollectionChanged -= Items_CollectionChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_ownsViewModel)
            _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _headerClickItem = null;
        _interactionVersion++;
        base.OnPreviewMouseDown(e);
    }

    protected override void SaveWindowState()
    {
        var bounds = GetNormalWindowBounds();
        _settingsService.SaveChecklistWindowState(new ChecklistWindowState
        {
            Left            = bounds.Left,
            Top             = bounds.Top,
            Width           = bounds.Width,
            Height          = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            Opacity         = _defaultOpacity,
            GhostModeOpacity = _ghostModeOpacity,
            GhostModeEnabled = _ghostModeEnabled,
            ReopenOnStartup = _reopenOnStartup,
            SelectedTabId = _viewModel.SelectedTab?.Id,
            PriorityColors = _priorityColors
        });
    }

    internal void CancelPreparedClose() => _preserveOpenStateOnClose = false;

    internal void PrepareForApplicationShutdown()
    {
        _editingSession.Checkpoint();
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        SaveWindowState();
        _preserveOpenStateOnClose = true;
    }

    // Title bar

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    protected override void RequestHide()
    {
        TryHide();
    }

    internal bool TryHide()
    {
        if (_completingItem || HasVisibleOwnedWindow) return false;
        var edits = _viewModel.Items.Where(_viewModel.IsItemEditing).ToArray();
        if (edits.Length > 0)
        {
            if (!TryFinishItems(edits, keepEmpty: false, focusItem: _commandItem)) return false;
            if (_viewModel.Items.Any(_viewModel.IsItemEditing)) return false;
        }
        else if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return false;
        }

        ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        _editingSession.Checkpoint();
        HideWindow();
        return true;
    }

    private void DueClockChanged(object? sender, EventArgs e)
    {
        if (!_isClosed && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(() => RefreshDueIndicators(this, EventArgs.Empty));
    }

    private void UpdateDueStatusTimer()
    {
        if (!_isClosed && IsVisible && _viewModel.Items.Any(item => item.DueDate.HasValue)) _dueStatusTimer.Start();
        else _dueStatusTimer.Stop();
    }

    private void RefreshDueIndicators(object? sender, EventArgs e)
    {
        UpdateDueStatusTimer();
        if (_isClosed || !IsVisible) return;
        foreach (var item in _viewModel.Items.Where(item => item.DueDate.HasValue)) item.RefreshDueStatus();
    }

    private void ChecklistWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        RefreshDueIndicators(this, EventArgs.Empty);
        if (!_preserveOpenStateOnClose && !IsChangingGroupVisibility)
        {
            _reopenOnStartup = IsWindowVisible;
            SaveWindowState();
        }

        if (!IsVisible)
        {
            if (_reminderTaskMenu is not null) _reminderTaskMenu.IsOpen = false;
            if (!TryFlushPendingContent(out var error))
                ShowPersistenceWarningOnce(error);
            else
                ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        }
    }

    private void ShowPersistenceWarningOnce(string? error)
    {
        if (string.IsNullOrWhiteSpace(error) || error == _lastPersistenceWarning)
            return;

        _lastPersistenceWarning = error;
        AppDialog.Show(this,
            error,
            "Task save warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSearchVisible = !_viewModel.IsSearchVisible;
        if (_viewModel.IsSearchVisible)
            Dispatcher.BeginInvoke(() => SearchBox.Focus(),
                System.Windows.Threading.DispatcherPriority.Input);
        else
            _viewModel.SearchQuery = "";
    }

    private void FindMenuItem_Click(object sender, RoutedEventArgs e) =>
        HandleSearchShortcut(Key.F, ModifierKeys.Control);

    private void SaveMenuItem_Click(object sender, RoutedEventArgs e) => SaveItems();

    private void SaveItems()
    {
        if (!_viewModel.TrySaveItems(null, out var error))
            ShowPersistenceWarningOnce(error);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        _headerClickItem = null;
        _interactionVersion++;
        var modifiers = e.KeyboardDevice.Modifiers;
        var item = GetItem(e.OriginalSource as DependencyObject);
        var popupItem = item ?? _commandItem;
        if (!e.Handled && e.Key == Key.Escape && e.IsRepeat)
            e.Handled = true;
        if (!e.Handled && !TabNamePopup.IsOpen && (popupItem is null || !IsItemPopupOpen(popupItem)))
        {
            if (e.Key == Key.S && modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) SaveItems();
                e.Handled = true;
            }
            else if (e.Key == Key.N && modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) BeginNewItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && (item ?? _commandItem) is { } edit && _viewModel.IsItemEditing(edit))
            {
                TryFinishItem(edit, keepEmpty: true, focusList: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && item is not null &&
                     (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
            {
                NavigateItem(item, e.OriginalSource as UIElement, modifiers == ModifierKeys.Shift);
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && modifiers == ModifierKeys.None &&
                     e.OriginalSource is DependencyObject source &&
                     VisualTreeHelpers.FindAncestor<RadioButton>(source) is RadioButton tabButton &&
                     tabButton.DataContext is ChecklistTab tab && tab.CanDelete)
            {
                if (!e.IsRepeat) OpenTabNamePopup(tab, tabButton);
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && modifiers == ModifierKeys.None && item is not null &&
                     !IsEditor(e.OriginalSource as DependencyObject))
            {
                if (!e.IsRepeat)
                {
                    if (item.IsEditingTitle) FocusTitle(item, selectAll: false);
                    else BeginTitleEditing(item);
                }
                e.Handled = true;
            }
            else if (e.OriginalSource is ListBox or ListBoxItem)
            {
                var selected = item ?? ItemsList.SelectedItem as ChecklistItem;
                if (modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down)
                {
                    var index = selected is null ? -1 : _viewModel.FilteredItems.IndexOf(selected);
                    var next = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0,
                        Math.Max(0, _viewModel.FilteredItems.Count - 1));
                    if (_viewModel.FilteredItems.Count > 0) FocusRow(_viewModel.FilteredItems[next]);
                    e.Handled = true;
                }
                else if (selected is not null && modifiers == ModifierKeys.None &&
                         e.Key is Key.Enter or Key.Space or Key.F2)
                {
                    if (!e.IsRepeat)
                    {
                        if (e.Key == Key.Enter) ToggleDetails(selected);
                        else if (e.Key == Key.Space) selected.IsChecked = !selected.IsChecked;
                        else BeginTitleEditing(selected);
                    }
                    e.Handled = true;
                }
                else if (selected is not null &&
                         (e.Key == Key.Apps || e.Key == Key.F10 && modifiers == ModifierKeys.Shift))
                {
                    OpenItemContextMenu(selected);
                    e.Handled = true;
                }
                else if (e.Key == Key.Tab &&
                         (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
                {
                    if (selected is null) MoveOutsideList(modifiers == ModifierKeys.Shift);
                    else NavigateItem(selected, e.OriginalSource as UIElement, modifiers == ModifierKeys.Shift);
                    e.Handled = true;
                }
                else e.Handled = HandleSearchShortcut(e.Key, modifiers);
            }
            else
                e.Handled = HandleSearchShortcut(e.Key, modifiers);
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            !TabNamePopup.IsOpen)
        {
            var item = GetItem(e.OriginalSource as DependencyObject);
            var popupItem = item ?? _commandItem;
            if (popupItem is null || !IsItemPopupOpen(popupItem))
            {
                var active = item ?? _commandItem;
                if (active is not null && _viewModel.IsItemEditing(active))
                    TryFinishItem(active, keepEmpty: false, focusList: true);
                else
                    RequestHide();
                e.Handled = true;
            }
        }
        base.OnKeyDown(e);
    }

    internal bool HandleSearchShortcut(Key key, ModifierKeys modifiers)
    {
        if (key != Key.F || modifiers != ModifierKeys.Control)
            return false;

        _viewModel.IsSearchVisible = true;
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        return true;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.IsSearchVisible = false;
            _viewModel.SearchQuery = "";
            e.Handled = true;
        }
    }

    // Tabs

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChecklistTab tab })
            _viewModel.SelectTab(tab);
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
        => OpenTabNamePopup(null, AddTabButton);

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var tab = GetTabFromContextMenu(menuItem);
        var target = GetContextMenu(menuItem)?.PlacementTarget as UIElement ?? AddTabButton;
        if (tab?.CanDelete == true)
            OpenTabNamePopup(tab, target);
    }

    private void DeleteTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromContextMenu(sender as MenuItem);
        if (tab?.CanDelete != true) return;

        var result = AppDialog.Show(
            this,
            $"Delete the '{tab.Name}' tab? Its tasks will remain available in All.",
            "Delete task tab",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (!_viewModel.TryDeleteTab(tab))
            ShowPersistenceWarningOnce(_viewModel.PersistenceError);
    }

    private void HideDefaultTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabFromContextMenu(sender as MenuItem);
        if (tab?.IsSystem != true) return;
        if (!_viewModel.SetDefaultTabVisibility(tab.Id, false))
            ShowPersistenceWarningOnce(_viewModel.PersistenceError);
    }

    private void ShowDefaultTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var id = menuItem.Tag?.ToString() ?? "";
        if (id.StartsWith("Show:", StringComparison.Ordinal))
            id = id[5..];
        if (!_viewModel.SetDefaultTabVisibility(id, true))
            ShowPersistenceWarningOnce(_viewModel.PersistenceError);
    }

    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu) return;
        var tab = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as ChecklistTab;
        var hasHiddenDefault = _viewModel.Tabs.Any(item => item.IsSystem && !item.IsVisible);

        foreach (var element in contextMenu.Items.OfType<FrameworkElement>())
        {
            var tag = element.Tag?.ToString();
            if (tag == "Rename" || tag == "Delete")
                element.Visibility = tab?.CanDelete == true ? Visibility.Visible : Visibility.Collapsed;
            else if (tag == "Hide")
                element.Visibility = tab?.IsSystem == true ? Visibility.Visible : Visibility.Collapsed;
            else if (tag == "HiddenTabsSeparator")
                element.Visibility = hasHiddenDefault ? Visibility.Visible : Visibility.Collapsed;
            else if (element is MenuItem menuItem && tag?.StartsWith("Show:", StringComparison.Ordinal) == true)
                UpdateShowDefaultMenuItem(menuItem, tag[5..], collapseWhenVisible: true);
        }
    }

    private void TabBarContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu) return;
        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
            UpdateShowDefaultMenuItem(menuItem, menuItem.Tag?.ToString() ?? "", collapseWhenVisible: false);
    }

    private void UpdateShowDefaultMenuItem(MenuItem menuItem, string id, bool collapseWhenVisible)
    {
        var tab = _viewModel.FindTab(id);
        if (tab is null)
        {
            menuItem.Visibility = Visibility.Collapsed;
            return;
        }

        menuItem.Visibility = collapseWhenVisible && tab.IsVisible
            ? Visibility.Collapsed
            : Visibility.Visible;
        menuItem.IsEnabled = !tab.IsVisible;
        menuItem.Header = tab.IsVisible ? $"{tab.Name} tab is shown" : $"Show {tab.Name} tab";
    }

    private void TabsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabsScrollViewer.ExtentWidth <= TabsScrollViewer.ViewportWidth) return;
        TabsScrollViewer.ScrollToHorizontalOffset(
            TabsScrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OpenTabNamePopup(ChecklistTab? tab, UIElement placementTarget)
    {
        _tabBeingRenamed = tab;
        TabNamePrompt.Text = tab is null ? "New tab" : "Rename tab";
        TabNamePrompt.Foreground = (Brush)FindResource("NotedAccentBrush");
        TabNameTextBox.Text = tab?.Name ?? "";
        ConfirmTabNameButton.Content = tab is null ? "Add" : "Save";
        TabNamePopup.PlacementTarget = placementTarget;
        TabNamePopup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            TabNameTextBox.Focus();
            TabNameTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ConfirmTabName_Click(object sender, RoutedEventArgs e)
        => TryCommitTabName();

    private void CancelTabName_Click(object sender, RoutedEventArgs e)
        => CloseTabNamePopup();

    private void TabNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryCommitTabName();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseTabNamePopup();
            e.Handled = true;
        }
    }

    private void TryCommitTabName()
    {
        var name = TabNameTextBox.Text.Trim();
        if (!_viewModel.IsTabNameAvailable(name, _tabBeingRenamed))
        {
            TabNamePrompt.Text = string.IsNullOrWhiteSpace(name)
                ? "Enter a tab name"
                : "That tab name is already in use";
            TabNamePrompt.Foreground = (Brush)FindResource("NotedDangerBrush");
            return;
        }

        var saved = _tabBeingRenamed is null
            ? _viewModel.TryCreateTab(name, out _)
            : _viewModel.TryRenameTab(_tabBeingRenamed, name);
        if (!saved)
        {
            TabNamePrompt.Text = "The tab could not be saved";
            TabNamePrompt.Foreground = (Brush)FindResource("NotedDangerBrush");
            ShowPersistenceWarningOnce(_viewModel.PersistenceError);
            return;
        }

        CloseTabNamePopup();
        TabsScrollViewer.ScrollToRightEnd();
    }

    private void CloseTabNamePopup()
    {
        TabNamePopup.IsOpen = false;
        _tabBeingRenamed = null;
    }

    // Add / delete

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        BeginNewItem();
    }

    private void DeleteItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = sender is MenuItem menuItem
            ? GetItemFromContextMenu(menuItem)
            : GetItemFromSender(sender);
        if (item != null) _viewModel.RemoveItem(item);
    }

    // Expand / collapse

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item != null) ToggleDetails(item);
    }

    private void ExpandedItemBackground_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is not { IsExpanded: true } item) return;
        if (_viewModel.IsItemEditing(item)) FocusRow(item);
        else item.IsExpanded = false;
    }

    private void ToggleDetails(ChecklistItem item)
    {
        if (item.IsExpanded && _viewModel.IsItemEditing(item))
        {
            TryFinishItem(item, keepEmpty: false, focusList: true);
            return;
        }
        item.IsExpanded = !item.IsExpanded;
    }

    private void EditTitle_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromContextMenu(sender as MenuItem);
        if (item is not null) BeginTitleEditing(item);
    }

    private void DueDateChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item is { IsChecked: false }) EditReminder(item);
        e.Handled = true;
    }

    private void ScheduleChip_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (!e.IsRepeat && GetItemFromSender(sender) is { IsChecked: false } item) EditReminder(item);
        e.Handled = true;
    }

    // Priority

    private void PriorityDot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item == null) return;
        item.Priority = item.Priority switch
        {
            ChecklistPriority.None   => ChecklistPriority.High,
            ChecklistPriority.High   => ChecklistPriority.Medium,
            ChecklistPriority.Medium => ChecklistPriority.Low,
            ChecklistPriority.Low    => ChecklistPriority.None,
            _                        => ChecklistPriority.None
        };
        e.Handled = true;
    }

    private void SetPriority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var item = GetItemFromContextMenu(mi);
        if (item == null) return;
        item.Priority = mi.Tag?.ToString() switch
        {
            "High"   => ChecklistPriority.High,
            "Medium" => ChecklistPriority.Medium,
            "Low"    => ChecklistPriority.Low,
            _        => ChecklistPriority.None
        };
    }

    private void PriorityColorsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChecklistPriorityColorsDialog(_priorityColors)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        _priorityColors = ChecklistColors.Normalize(dialog.SelectedColors);
        ApplyPriorityColors();
        SaveWindowState();
    }

    private void ApplyPriorityColors()
    {
        Resources["ChecklistHighPriorityBrush"] = CreatePriorityBrush(_priorityColors.HighColor);
        Resources["ChecklistMediumPriorityBrush"] = CreatePriorityBrush(_priorityColors.MediumColor);
        Resources["ChecklistLowPriorityBrush"] = CreatePriorityBrush(_priorityColors.LowColor);
    }

    private static SolidColorBrush CreatePriorityBrush(string colorValue)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorValue));
        brush.Freeze();
        return brush;
    }

    // Context menu reorder / duplicate

    private void MoveToTop_Click(object sender, RoutedEventArgs e)
        => _viewModel.MoveToTop(GetItemFromContextMenu(sender as MenuItem)!);

    private void MoveUp_Click(object sender, RoutedEventArgs e)
        => _viewModel.MoveItemUp(GetItemFromContextMenu(sender as MenuItem)!);

    private void MoveDown_Click(object sender, RoutedEventArgs e)
        => _viewModel.MoveItemDown(GetItemFromContextMenu(sender as MenuItem)!);

    private void MoveToBottom_Click(object sender, RoutedEventArgs e)
        => _viewModel.MoveToBottom(GetItemFromContextMenu(sender as MenuItem)!);

    private void ItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu) return;
        AddMoveToTabItems(contextMenu);
        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>()
                     .Where(item => Equals(item.Tag, "ManualReorder")))
        {
            menuItem.IsEnabled = _viewModel.CanManuallyReorder;
        }
    }

    private void ChecklistItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } contextMenu } itemRoot)
            return;

        contextMenu.PlacementTarget = itemRoot;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void DuplicateItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromContextMenu(sender as MenuItem);
        if (item != null) _viewModel.DuplicateItem(item);
    }

    // Bulk actions

    private void ClearCompleted_Click(object sender, RoutedEventArgs e) => _viewModel.ClearCompleted();
    private void CheckAll_Click(object sender, RoutedEventArgs e)       => _viewModel.CheckAll();
    private void UncheckAll_Click(object sender, RoutedEventArgs e)     => _viewModel.UncheckAll();

    // Keyboard navigation

    private void BeginNewItem(ChecklistItem? anchor = null)
    {
        if (!_viewModel.CanAddItem) return;
        var item = _viewModel.AddItem(anchor);
        if (item is null) return;
        _creations.Add(item, (item.Priority, item.TabId));
        item.IsEditingTitle = true;
        StartEditing(item);
        var version = ++_interactionVersion;
        if (!_viewModel.FilteredItems.Contains(item)) _viewModel.RefreshFilteredItems(force: true);
        Dispatcher.BeginInvoke(() =>
        {
            if (version == _interactionVersion && _creations.ContainsKey(item) && _viewModel.FilteredItems.Contains(item))
                FocusTitle(item, selectAll: false);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool IsEmptyCreation(ChecklistItem item) =>
        _creations.TryGetValue(item, out var creation) &&
        string.IsNullOrWhiteSpace(item.Text) && string.IsNullOrWhiteSpace(item.Notes) &&
        item.DueDate is null && item.ReminderAtUtc is null && !item.IsChecked &&
        item.Priority == creation.Priority && item.TabId == creation.TabId;

    private void StartEditing(ChecklistItem item)
    {
        if (!ReferenceEquals(_commandItem, item)) _sessionEditor = UtilityEditorField.None;
        _commandItem = item;
        _viewModel.StartItemEdit(item);
        _editingSession.Schedule();
    }

    private void EndEditing(ChecklistItem item)
    {
        _creations.Remove(item);
        _editorSelections.Forget(item);
        if (ReferenceEquals(_lastEditorFocus?.Item, item)) _lastEditorFocus = null;
        _viewModel.EndItemEdit(item);
        item.IsEditingTitle = false;
        _editingSession.Schedule();
    }

    private bool TryFinishItem(ChecklistItem item, bool keepEmpty, bool focusList, bool collapse = true) =>
        TryFinishItems([item], keepEmpty, focusList ? item : null, collapse);

    private bool TryFinishItems(IReadOnlyList<ChecklistItem> items, bool keepEmpty, ChecklistItem? focusItem, bool collapse = true)
    {
        if (_completingItem) return false;
        // warning dialogs can dispatch focus changes before this completion has returned.
        _completingItem = true;
        var finished = false;
        try
        {
            var destination = CaptureFocus();
            if (destination?.Item is { } active && items.Contains(active) && IsEditor(destination.Element))
                _lastEditorFocus = destination;
            var index = focusItem is null ? -1 : _viewModel.FilteredItems.IndexOf(focusItem);
            var emptyCreations = items.Where(IsEmptyCreation).ToHashSet();
            if (keepEmpty)
            {
                foreach (var item in emptyCreations)
                    if (!_editingSession.TryRetireChecklistCreation(item.Id, out var sessionError))
                        return SaveFailed(sessionError);
                emptyCreations.Clear();
            }
            if (!_viewModel.TrySaveItems(emptyCreations, out var error)) return SaveFailed(error);
            // a save callback can populate a placeholder excluded from the first snapshot.
            if (emptyCreations.RemoveWhere(item => !IsEmptyCreation(item)) > 0)
            {
                if (!_viewModel.TrySaveItems(emptyCreations, out error)) return SaveFailed(error);
                if (emptyCreations.Any(item => !IsEmptyCreation(item)))
                    return SaveFailed("Task content changed while saving. Try again to save the latest changes.");
            }

            _viewModel.DeferContentRefresh(true);
            foreach (var item in items)
            {
                EndEditing(item);
                if (collapse) item.IsExpanded = false;
                if (emptyCreations.Contains(item)) _viewModel.RemoveItem(item);
            }
            _viewModel.DeferContentRefresh(false, refresh: false);
            _viewModel.RefreshFilteredItems();
            if (focusItem is not null) FocusRow(focusItem, index);
            else if (destination?.Item is { } target && items.Contains(target)) FocusRow(target);
            else if (destination is not null) RestoreFocus(destination);
            finished = true;
            return true;

            bool SaveFailed(string? error)
            {
                ShowPersistenceWarningOnce(error);
                if (destination?.Item is { } failed && items.Contains(failed)) RestoreEditorFocus(failed);
                else if (_lastEditorFocus?.Item is { } last && items.Contains(last)) RestoreEditorFocus(last);
                else if (destination is not null) RestoreFocus(destination);
                return false;
            }
        }
        finally
        {
            _completingItem = false;
            if (finished && _pendingReminderActivation is not null)
                Dispatcher.BeginInvoke(TryOpenPendingReminder, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void NavigateItem(ChecklistItem item, UIElement? source, bool backward)
    {
        if (source is null) return;
        if (!backward && source is TextBox { Name: "ItemTitleTextBox" })
        {
            item.IsExpanded = true;
            ItemsList.UpdateLayout();
            FindItemControl<TextBox>(item, "NotesTextBox")?.Focus();
            return;
        }

        var controls = GetTabControls(item);
        var index = controls.FindIndex(control => ReferenceEquals(control, source));
        if (index < 0)
            index = controls.FindIndex(control => control is not ListBoxItem && IsWithin(source, control));
        var next = index + (backward ? -1 : 1);
        if (next >= 0 && next < controls.Count) controls[next].Focus();
        else MoveOutsideList(backward);
    }

    private List<UIElement> GetTabControls(ChecklistItem item)
    {
        var controls = new List<UIElement>();
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row) controls.Add(row);
        foreach (var name in new[] { "ItemDetailsButton", "ItemCompletionCheckBox", "ItemTitleTextBox", "NotesTextBox", "ItemReminderButton", "ItemDueDateChip", "ItemNotificationButton" })
        {
            var control = FindItemControl<UIElement>(item, name);
            if (control is { IsVisible: true, IsEnabled: true, Focusable: true }) controls.Add(control);
        }
        return controls;
    }

    private void MoveOutsideList(bool backward)
    {
        var navigation = KeyboardNavigation.GetTabNavigation(ItemsList);
        KeyboardNavigation.SetTabNavigation(ItemsList, KeyboardNavigationMode.None);
        try
        {
            ItemsList.MoveFocus(new TraversalRequest(backward ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        finally { KeyboardNavigation.SetTabNavigation(ItemsList, navigation); }
    }

    private static bool IsEditor(DependencyObject? element) =>
        element is TextBox editor && EditorField(editor) != UtilityEditorField.None;

    private const string TitleEditor = "ItemTitleTextBox";
    private const string NotesEditor = "NotesTextBox";

    private static UtilityEditorField EditorField(TextBox editor) => editor.Name switch
    {
        TitleEditor => UtilityEditorField.Primary,
        NotesEditor => UtilityEditorField.Details,
        _ => UtilityEditorField.None
    };

    private void SessionEditor_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor && editor.DataContext is ChecklistItem item)
            _editorSelections.Restore(item, editor);
    }

    private IReadOnlyList<UtilityItemSession> CaptureEditingSession()
    {
        if (IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox editor && IsEditor(editor) &&
            editor.DataContext is ChecklistItem focused)
            _editorSelections.Remember(focused, editor);

        var session = new List<UtilityItemSession>();
        foreach (var item in _viewModel.Items)
        {
            var editing = _viewModel.IsItemEditing(item);
            var active = ReferenceEquals(item, _commandItem) ||
                _commandItem is null && _pendingSessionFocus?.ItemId == item.Id;
            if (!editing && !active && !item.IsExpanded) continue;
            var creating = _creations.TryGetValue(item, out var creation);
            session.Add(new()
            {
                ItemId = item.Id,
                IsExpanded = item.IsExpanded,
                IsActive = active,
                ActiveEditor = active && editing ? _pendingSessionFocus?.ActiveEditor ?? _sessionEditor : UtilityEditorField.None,
                Primary = _editorSelections.Get(item, TitleEditor),
                Details = _editorSelections.Get(item, NotesEditor),
                ChecklistEdit = editing ? new(item.IsEditingTitle, _viewModel.GetEditOriginTabId(item),
                    ItemEditorSelections<ChecklistItem>.CaptureText(item.Text, item.Notes),
                    creating ? new(creation.Priority, creation.TabId) : null) : null
            });
        }
        return session;
    }

    private void RestoreEditingSession()
    {
        var items = _viewModel.Items.ToDictionary(item => item.Id);
        foreach (var saved in _editingSession.Loaded)
        {
            if (!items.TryGetValue(saved.ItemId, out var item)) continue;
            item.IsExpanded = saved.IsExpanded;
            if (saved.ChecklistEdit is { } edit)
            {
                _viewModel.RestoreItemEdit(item, edit.OriginTabId);
                item.IsEditingTitle = edit.IsEditingTitle;
                if (edit.Creation is { } creation &&
                    edit.Text == ItemEditorSelections<ChecklistItem>.CaptureText(item.Text, item.Notes))
                    _creations.Add(item, (creation.Priority, creation.TabId));
                _editorSelections.Load(item, TitleEditor, saved.Primary);
                _editorSelections.Load(item, NotesEditor, saved.Details);
            }
            if (saved.IsActive) _pendingSessionFocus = saved;
        }
        if (items.Count > 0 && _editingSession.Loaded.Count > 0)
            _viewModel.RefreshFilteredItems(force: true);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_pendingSessionFocus is not { } saved) return;
        _pendingSessionFocus = null;
        var item = _viewModel.FilteredItems.FirstOrDefault(item => item.Id == saved.ItemId);
        if (item is null) return;
        ItemsList.ScrollIntoView(item);
        ItemsList.UpdateLayout();
        var editor = saved.ActiveEditor switch
        {
            UtilityEditorField.Primary => FindItemControl<TextBox>(item, TitleEditor),
            UtilityEditorField.Details => FindItemControl<TextBox>(item, NotesEditor),
            _ => null
        };
        if (editor is not { IsVisible: true } || !_editorSelections.Restore(item, editor) || !editor.Focus())
            FocusRow(item);
    }

    private void Item_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_completingItem && e.OldFocus is TextBox editor && IsEditor(editor) &&
            editor.DataContext is ChecklistItem item && _viewModel.IsItemEditing(item))
        {
            _editorSelections.Remember(item, editor);
            _lastEditorFocus = CaptureFocus(editor);
        }
    }

    private void Item_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_completingItem || !IsEditor(e.OldFocus as DependencyObject)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!(IsKeyboardFocusWithin && IsEditor(Keyboard.FocusedElement as DependencyObject)))
                _viewModel.DeferContentRefresh(false);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BeginTitleEditing(ChecklistItem item, Point? clickPosition = null)
    {
        if (!_viewModel.FilteredItems.Contains(item)) return;
        StartEditing(item);
        item.IsEditingTitle = true;
        FocusTitle(item, selectAll: clickPosition is null, clickPosition);
    }

    private void FocusTitle(ChecklistItem item, bool selectAll, Point? clickPosition = null)
    {
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        ItemsList.UpdateLayout();
        var editor = FindItemControl<TextBox>(item, "ItemTitleTextBox");
        if (editor?.Focus() != true) return;
        if (selectAll) editor.SelectAll();
        else if (clickPosition is Point point)
        {
            editor.ScrollToHorizontalOffset(0);
            editor.UpdateLayout();
            var index = editor.GetCharacterIndexFromPoint(
                new Point(point.X + editor.Padding.Left, editor.ActualHeight / 2), snapToText: true);
            editor.CaretIndex = index >= 0 ? index : editor.Text.Length;
        }
        else if (!_editorSelections.Restore(item, editor)) editor.CaretIndex = editor.Text.Length;
    }

    private void RestoreEditorFocus(ChecklistItem item)
    {
        if (_lastEditorFocus is { } target && ReferenceEquals(target.Item, item)) RestoreFocus(target);
        else if (item.IsEditingTitle) FocusTitle(item, selectAll: false);
        else
        {
            item.IsExpanded = true;
            ItemsList.UpdateLayout();
            FindItemControl<TextBox>(item, "NotesTextBox")?.Focus();
        }
    }

    private void FocusRow(ChecklistItem item, int index = -1)
    {
        if (!_viewModel.FilteredItems.Contains(item))
        {
            if (_viewModel.FilteredItems.Count == 0)
            {
                ItemsList.SelectedItem = null;
                ItemsList.Focus();
                return;
            }
            item = _viewModel.FilteredItems[Math.Clamp(index, 0, _viewModel.FilteredItems.Count - 1)];
        }
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
        ItemsList.UpdateLayout();
        (ItemsList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
    }

    private void ItemsList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is FrameworkElement element && GetItem(element) is ChecklistItem selected)
        {
            ItemsList.SelectedItem = selected;
            _commandItem = selected;
            _sessionEditor = element is TextBox box ? EditorField(box) : UtilityEditorField.None;
            _editingSession.Schedule();
            if (!_completingItem && element is TextBox editor && IsEditor(editor))
            {
                StartEditing(selected);
                _editorSelections.Restore(selected, editor);
                _viewModel.DeferContentRefresh(true);
            }
        }
        if (ReferenceEquals(e.NewFocus, ItemsList) && ItemsList.SelectedItem is ChecklistItem item)
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isClosed && ReferenceEquals(Keyboard.FocusedElement, ItemsList) &&
                    ReferenceEquals(ItemsList.SelectedItem, item)) FocusRow(item);
            }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private FocusTarget? CaptureFocus(FrameworkElement? element = null)
    {
        element ??= Keyboard.FocusedElement as FrameworkElement;
        if (element is null) return null;
        var item = GetItem(element);
        return new FocusTarget(element, item,
            element.Name,
            item is null ? -1 : _viewModel.FilteredItems.IndexOf(item),
            (element as TextBox)?.SelectionStart ?? 0, (element as TextBox)?.SelectionLength ?? 0);
    }

    private void RestoreFocus(FocusTarget target)
    {
        if (target.Item is null)
        {
            if (target.Element.IsVisible && target.Element.IsEnabled) target.Element.Focus();
            return;
        }
        if (!_viewModel.FilteredItems.Contains(target.Item))
        {
            FocusRow(target.Item, target.Index);
            return;
        }
        if (VisualTreeHelpers.FindAncestor<ContextMenu>(target.Element)?.IsOpen == true)
        {
            target.Element.Focus();
            return;
        }
        ItemsList.UpdateLayout();
        var control = target.Element is ListBoxItem ?
            ItemsList.ItemContainerGenerator.ContainerFromItem(target.Item) as FrameworkElement :
            FindItemControl<FrameworkElement>(target.Item, target.Name);
        if (control?.IsVisible == true && (control.IsKeyboardFocused || control.Focus()))
        {
            if (control is TextBox editor) ItemEditorSelections<ChecklistItem>.Apply(editor, target.Start, target.Length);
        }
        else FocusRow(target.Item);
    }

    private void ViewModel_ViewRefreshing(object? sender, EventArgs e)
    {
        _refreshFocus = _completingItem ? null : CaptureFocus();
    }

    private void ViewModel_ViewRefreshed(object? sender, EventArgs e)
    {
        var target = _refreshFocus;
        _refreshFocus = null;
        if (target?.Item is null) return;
        var version = _interactionVersion;
        Dispatcher.BeginInvoke(() =>
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (IsVisible && IsActive && version == _interactionVersion &&
                (focused is null || ReferenceEquals(focused, target.Element) || ReferenceEquals(focused, ItemsList) ||
                 focused is ListBoxItem row && ReferenceEquals(row.DataContext, target.Item)))
                RestoreFocus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        foreach (var item in e.OldItems?.OfType<ChecklistItem>() ?? [])
            EndEditing(item);
        if (_commandItem is not null && !_viewModel.Items.Contains(_commandItem))
            _commandItem = null;
    }

    private void OpenItemContextMenu(ChecklistItem item)
    {
        var root = FindItemControl<Grid>(item, "ChecklistItemRoot");
        if (root?.ContextMenu is not { } menu) return;
        menu.PlacementTarget = root;
        menu.IsOpen = true;
    }

    private T? FindItemControl<T>(ChecklistItem item, string name) where T : DependencyObject
    {
        return VisualTreeHelpers.FindDescendant<T>(ItemsList.ItemContainerGenerator.ContainerFromItem(item), name);
    }

    private bool IsItemPopupOpen(ChecklistItem item) =>
        FindItemControl<Grid>(item, "ChecklistItemRoot")?.ContextMenu?.IsOpen == true;

    private static ChecklistItem? GetItem(DependencyObject? target)
    {
        var element = VisualTreeHelpers.FindAncestor<FrameworkElement>(target);
        if (element?.DataContext is ChecklistItem item) return item;
        var menu = VisualTreeHelpers.FindAncestor<ContextMenu>(target);
        return (menu?.PlacementTarget as FrameworkElement)?.DataContext as ChecklistItem;
    }

    private static bool IsWithin(DependencyObject? target, DependencyObject ancestor)
    {
        return ReferenceEquals(target, ancestor) ||
            target is Visual visual && ancestor is Visual parent && parent.IsAncestorOf(visual);
    }

    private void ItemTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.DataContext is ChecklistItem { IsEditingTitle: true } item)
            _viewModel.UpdateItemText(item, textBox.Text);
    }

    private void ItemTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChecklistItem item) return;

        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            if (_creations.ContainsKey(item))
            {
                if (TryFinishItem(item, keepEmpty: true, focusList: false)) BeginNewItem(item);
            }
            else TryFinishItem(item, keepEmpty: true, focusList: true, collapse: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!e.IsRepeat) TryFinishItem(item, keepEmpty: false, focusList: true);
            e.Handled = true;
        }
    }

    private void NotesTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled && sender is TextBox editor)
            e.Handled = EditorTextWrapping.HandleKey(editor, e.Key, e.KeyboardDevice.Modifiers);
    }

    // Drag-drop reordering

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearPendingDrag();
        _headerClickItem = null;
        _ignoreHeaderClick = e.ClickCount > 1;
        var dragArea = GetHeaderBackground(e.OriginalSource as DependencyObject);
        if (dragArea?.DataContext is not ChecklistItem item) return;
        _headerClickItem = item;
        _headerClickEditsTitle = item.IsExpanded &&
            VisualTreeHelpers.FindAncestor<TextBlock>(e.OriginalSource as DependencyObject)?.Name == "ItemTitleText";
        _dragStartPoint = e.GetPosition(ItemsList);
        if (!_viewModel.CanManuallyReorder) return;

        var container = VisualTreeHelpers.FindAncestor<ListBoxItem>(dragArea);
        if (container is null) return;

        _pendingDragItem = item;
        _pendingDragContainer = container;
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _pendingDragItem is null ||
            _pendingDragContainer is null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                ClearPendingDrag();
            return;
        }

        var currentPoint = e.GetPosition(ItemsList);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _draggedItem = _pendingDragItem;
        _draggedContainer = _pendingDragContainer;
        _ignoreHeaderClick = true;
        _headerClickItem = null;
        ClearPendingDrag();

        try
        {
            ShowDragPreview(_draggedItem, _draggedContainer);
            DragDrop.DoDragDrop(ItemsList, _draggedItem, DragDropEffects.Move);
        }
        finally
        {
            HideDragPreview();
            _draggedContainer = null;
            _draggedItem = null;
        }

        e.Handled = true;
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _headerClickItem;
        _headerClickItem = null;
        ClearPendingDrag();
        var point = e.GetPosition(ItemsList);
        if (_ignoreHeaderClick || item is null ||
            Math.Abs(point.X - _dragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(point.Y - _dragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance ||
            !ReferenceEquals(GetHeaderBackground(e.OriginalSource as DependencyObject)?.DataContext, item)) return;
        ItemsList.SelectedItem = item;
        if (_headerClickEditsTitle)
        {
            var title = FindItemControl<TextBlock>(item, "ItemTitleText");
            if (title is not null) BeginTitleEditing(item, e.GetPosition(title));
            e.Handled = true;
        }
        else ToggleDetails(item);
    }

    private static Grid? GetHeaderBackground(DependencyObject? source)
    {
        var header = VisualTreeHelpers.FindAncestor<Grid>(source);
        while (header is not null && header.Name != "ItemDragArea")
            header = VisualTreeHelpers.FindAncestor<Grid>(VisualTreeHelper.GetParent(header));
        if (header is null) return null;
        for (var current = source; current is not null && !ReferenceEquals(current, header); current = VisualTreeHelper.GetParent(current))
        {
            if (current is Control or System.Windows.Shapes.Ellipse ||
                current is FrameworkElement { Name: "ItemDueDateChip" }) return null;
        }
        return header;
    }

    private void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        UpdateDragPreviewPosition(e.GetPosition(ItemsList));
        e.Effects = _viewModel.CanManuallyReorder && _draggedItem != null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ItemsList_Drop(object sender, DragEventArgs e)
    {
        if (!_viewModel.CanManuallyReorder || _draggedItem == null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dropTarget = GetDropTarget(e.GetPosition(ItemsList));
        if (dropTarget is { } target && !ReferenceEquals(target.Item, _draggedItem))
            _viewModel.MoveItem(_draggedItem, target.Item, target.PlaceAfter);

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    // Helpers

    private static ChecklistItem? GetItemFromSender(object sender)
        => (sender as FrameworkElement)?.DataContext as ChecklistItem;

    private static ChecklistItem? GetItemFromContextMenu(MenuItem? menuItem)
    {
        if (menuItem == null) return null;
        DependencyObject? current = menuItem;
        while (current != null)
        {
            if (current is ContextMenu cm) return (cm.PlacementTarget as FrameworkElement)?.DataContext as ChecklistItem;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static ChecklistTab? GetTabFromContextMenu(MenuItem? menuItem)
        => (GetContextMenu(menuItem)?.PlacementTarget as FrameworkElement)?.DataContext as ChecklistTab;

    private static ContextMenu? GetContextMenu(MenuItem? menuItem)
    {
        if (menuItem is null) return null;
        DependencyObject? current = menuItem;
        while (current is not null)
        {
            if (current is ContextMenu contextMenu) return contextMenu;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private (ChecklistItem Item, bool PlaceAfter)? GetDropTarget(Point point)
    {
        ChecklistItem? lastItem = null;
        foreach (var item in _viewModel.FilteredItems)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                continue;

            lastItem = item;
            var top = container.TranslatePoint(new Point(0, 0), ItemsList).Y;
            if (point.Y < top + (container.ActualHeight / 2))
                return (item, false);
        }

        return lastItem is null ? null : (lastItem, true);
    }

    private void ClearPendingDrag()
    {
        _pendingDragItem = null;
        _pendingDragContainer = null;
    }

    private void ShowDragPreview(ChecklistItem item, ListBoxItem? container)
    {
        if (container is null || container.ActualWidth <= 0 || container.ActualHeight <= 0)
            return;

        DragPreviewPopup.DataContext = item;
        DragPreviewBorder.Width = Math.Max(160, ItemsList.ActualWidth - 14);
        DragPreviewPopup.IsOpen = true;
        UpdateDragPreviewPosition(Mouse.GetPosition(ItemsList));
        container.Opacity = 0.35;
    }

    private void UpdateDragPreviewPosition(Point pointer)
    {
        if (!DragPreviewPopup.IsOpen) return;
        const double pointerGap = 12;
        var previewHeight = DragPreviewBorder.ActualHeight > 0
            ? DragPreviewBorder.ActualHeight
            : DragPreviewBorder.Height;
        var availableHeight = Math.Max(0, ItemsList.ActualHeight - previewHeight);
        var previewTop = pointer.Y + pointerGap;
        if (previewTop + previewHeight > ItemsList.ActualHeight)
            previewTop = pointer.Y - previewHeight - pointerGap;

        DragPreviewPopup.HorizontalOffset = 7;
        DragPreviewPopup.VerticalOffset = Math.Clamp(previewTop, 0, availableHeight);
    }

    private void HideDragPreview()
    {
        if (_draggedContainer is not null)
            _draggedContainer.Opacity = 1;

        DragPreviewPopup.IsOpen = false;
        DragPreviewPopup.DataContext = null;
    }

    private void AddMoveToTabItems(ContextMenu contextMenu)
    {
        foreach (var existing in contextMenu.Items.OfType<MenuItem>()
                     .Where(item => item.Tag is MoveToTabCommand)
                     .ToList())
        {
            contextMenu.Items.Remove(existing);
        }

        var separator = contextMenu.Items.OfType<Separator>()
            .FirstOrDefault(item => Equals(item.Tag, "MoveTabsSeparator"));
        var item = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as ChecklistItem;
        if (separator is null || item is null) return;

        var customTabs = _viewModel.CustomTabs.OrderBy(tab => tab.Order).ToList();
        separator.Visibility = customTabs.Count > 0 || item.TabId is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (separator.Visibility != Visibility.Visible) return;

        var insertionIndex = contextMenu.Items.IndexOf(separator);
        AddMoveToTabMenuItem(contextMenu, insertionIndex++, item, null, "All");
        foreach (var tab in customTabs)
            AddMoveToTabMenuItem(contextMenu, insertionIndex++, item, tab.Id, tab.Name);
    }

    private void AddMoveToTabMenuItem(
        ContextMenu contextMenu,
        int index,
        ChecklistItem item,
        string? tabId,
        string tabName)
    {
        var menuItem = new MenuItem
        {
            Header = $"Move to {tabName}",
            Tag = new MoveToTabCommand(tabId),
            IsEnabled = !string.Equals(item.TabId, tabId, StringComparison.Ordinal),
            Style = FindResource("NotedContextMenuItem") as Style,
            Icon = new TextBlock
            {
                Text = "↪",
                Foreground = (Brush)FindResource("NotedAccentBrush"),
                FontSize = 12
            }
        };
        menuItem.Click += MoveItemToTab_Click;
        contextMenu.Items.Insert(index, menuItem);
    }

    private void MoveItemToTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MoveToTabCommand command } menuItem) return;
        var item = GetItemFromContextMenu(menuItem);
        if (item is not null)
            _viewModel.MoveItemToTab(item, command.TabId);
    }

    private sealed record MoveToTabCommand(string? TabId);
}
