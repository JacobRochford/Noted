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
    private ChecklistItem? _creatingItem;
    private ChecklistPriority _creationPriority;
    private string? _creationTabId;
    private ChecklistItem? _editingItem;
    private ChecklistItem? _titleSelectionItem;
    private int _titleSelectionStart;
    private int _titleSelectionLength;
    private bool _navigatingItem;
    private bool _completingItem;
    private FocusTarget? _lastEditorFocus;
    private FocusTarget? _refreshFocus;
    private ChecklistItem? _headerClickItem;
    private bool _headerClickEditsTitle;
    private bool _ignoreHeaderClick;
    private bool _isClosed;
    private int _interactionVersion;

    private sealed record FocusTarget(
        FrameworkElement Element, ChecklistItem? Item, string Name, int Index, int Start, int Length);

    public ChecklistWindow(
        IAppSettingsService settingsService,
        IChecklistContentService contentService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        var state = _settingsService.LoadChecklistWindowState();
        _priorityColors = ChecklistColors.Normalize(state.PriorityColors);
        ApplyPriorityColors();
        _reopenOnStartup = state.ReopenOnStartup;
        RestoreWindowBounds(state.Left, state.Top, state.Width, state.Height);

        _viewModel = new ChecklistWindowViewModel(
            contentService,
            Dispatcher,
            action => Dispatcher.Invoke(action),
            state.GhostModeEnabled);
        if (state.SelectedTabId is { } selectedTabId)
            _viewModel.SelectTab(_viewModel.FindTab(selectedTabId));
        DataContext = _viewModel;
        InitializeOverlay(state.GhostModeEnabled, state.GhostModeOpacity, state.Opacity);
        _viewModel.ViewChanging += ViewModel_ViewChanging;
        _viewModel.ViewRefreshing += ViewModel_ViewRefreshing;
        _viewModel.ViewRefreshed += ViewModel_ViewRefreshed;
        _viewModel.Items.CollectionChanged += Items_CollectionChanged;

        _viewModel.PropertyChanged += (s, e) =>
        {
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
        };
        IsVisibleChanged += ChecklistWindow_IsVisibleChanged;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        var active = _creatingItem ?? _editingItem;
        if (active is not null && !TryCommitDueDate(active))
        {
            e.Cancel = true;
            FocusDate(active);
            return;
        }
        if (!TryFlushPendingContent(out var error))
        {
            e.Cancel = true;
            ShowPersistenceWarningOnce(error);
            return;
        }

        if (!_preserveOpenStateOnClose)
            _reopenOnStartup = false;

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        if (_editingItem is not null) _editingItem.IsEditingTitle = false;
        _creatingItem = null;
        _editingItem = null;
        IsVisibleChanged -= ChecklistWindow_IsVisibleChanged;
        _viewModel.ViewChanging -= ViewModel_ViewChanging;
        _viewModel.ViewRefreshing -= ViewModel_ViewRefreshing;
        _viewModel.ViewRefreshed -= ViewModel_ViewRefreshed;
        _viewModel.Items.CollectionChanged -= Items_CollectionChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _headerClickItem = null;
        _interactionVersion++;
        var target = e.OriginalSource as DependencyObject;
        var picker = VisualTreeHelpers.FindAncestor<DatePicker>(target);
        if (e.ChangedButton == MouseButton.Left && picker is not null &&
            VisualTreeHelpers.FindAncestor<Button>(target) is not null &&
            GetItem(picker) is ChecklistItem datedItem && !TryCommitDueDate(datedItem))
        {
            e.Handled = true;
            FocusDate(datedItem);
            base.OnPreviewMouseDown(e);
            return;
        }
        var current = _creatingItem ?? _editingItem;
        if (e.ChangedButton == MouseButton.Left &&
            !IsWithin(target, TitleBar) && !IsWithin(target, AddItemButton) &&
            current is not null && !IsItemPopupOpen(current) &&
            !ReferenceEquals(GetItem(target), current))
        {
            var creation = _creatingItem;
            var editing = _editingItem;
            // let the click reach its original control before refreshing or removing rows.
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsVisible || !_viewModel.Items.Contains(current) ||
                    !ReferenceEquals(_creatingItem, creation) || !ReferenceEquals(_editingItem, editing)) return;
                FinishNavigation(current);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        base.OnPreviewMouseDown(e);
    }

    protected override void SaveWindowState()
    {
        _settingsService.SaveChecklistWindowState(new ChecklistWindowState
        {
            Left            = Left,
            Top             = Top,
            Width           = Width,
            Height          = Height,
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
        var item = _creatingItem ?? _editingItem;
        if (item is not null)
        {
            if (!TryFinishItem(item, keepEmpty: false, focusList: true)) return false;
        }
        else if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return false;
        }

        ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        HideWindow();
        return true;
    }

    private void ChecklistWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!_preserveOpenStateOnClose && !IsChangingGroupVisibility)
        {
            _reopenOnStartup = IsWindowVisible;
            SaveWindowState();
        }

        if (!IsVisible)
        {
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
        AppDialog.Show(
            error,
            "Checklist Save Warning",
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        _headerClickItem = null;
        _interactionVersion++;
        var modifiers = e.KeyboardDevice.Modifiers;
        var item = GetItem(e.OriginalSource as DependencyObject);
        var popupItem = item ?? _creatingItem ?? _editingItem;
        if (!e.Handled && e.Key == Key.Escape && e.IsRepeat)
            e.Handled = true;
        if (!e.Handled && e.Key == Key.Escape && modifiers == ModifierKeys.None &&
            !TabNamePopup.IsOpen && popupItem is not null)
        {
            var picker = FindItemControl<DatePicker>(popupItem, "ItemDueDatePicker");
            if (picker?.IsDropDownOpen == true)
            {
                picker.SetCurrentValue(DatePicker.IsDropDownOpenProperty, false);
                FocusDate(popupItem);
                e.Handled = true;
            }
        }
        if (!e.Handled && !TabNamePopup.IsOpen && (popupItem is null || !IsItemPopupOpen(popupItem)))
        {
            if (e.Key == Key.N && modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) BeginNewItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && item is not null &&
                     (ReferenceEquals(item, _creatingItem) || ReferenceEquals(item, _editingItem)))
            {
                TryFinishItem(item, keepEmpty: true, focusList: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && item is not null &&
                     (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
            {
                NavigateItem(item, e.OriginalSource as UIElement, modifiers == ModifierKeys.Shift);
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
            var popupItem = item ?? _creatingItem ?? _editingItem;
            if (popupItem is null || !IsItemPopupOpen(popupItem))
            {
                var active = _creatingItem ?? _editingItem;
                if (active is not null)
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
            "Delete Checklist Tab",
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

    private void ToggleDetails(ChecklistItem item)
    {
        if (item.IsExpanded && (ReferenceEquals(item, _creatingItem) || ReferenceEquals(item, _editingItem)))
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
        if (item != null) item.IsExpanded = true;
        e.Handled = true;
    }

    // Due date

    private void ClearDueDate_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item is null) return;
        var picker = FindItemControl<DatePicker>(item, "ItemDueDatePicker");
        if (picker is not null) picker.SetCurrentValue(DatePicker.TextProperty, "");
        item.DueDate = null;
        SetDateError(item, false);
    }

    private bool TryCommitDueDate(ChecklistItem item)
    {
        var picker = FindItemControl<DatePicker>(item, "ItemDueDatePicker");
        var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker);
        if (picker is null || editor is null || !picker.IsVisible) return true;
        DateTime? date = null;
        if (!string.IsNullOrWhiteSpace(editor.Text))
        {
            if (!DateTime.TryParse(editor.Text, picker.Language.GetSpecificCulture(), DateTimeStyles.None, out var parsed))
            {
                SetDateError(item, true);
                return false;
            }
            date = parsed.Date;
        }
        picker.SetCurrentValue(DatePicker.SelectedDateProperty, date);
        picker.GetBindingExpression(DatePicker.SelectedDateProperty)?.UpdateSource();
        SetDateError(item, false);
        return true;
    }

    private void SetDateError(ChecklistItem item, bool invalid)
    {
        var message = FindItemControl<TextBlock>(item, "DueDateErrorText");
        if (message is not null) message.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DueDateTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Space && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            sender is DatePickerTextBox editor &&
            (string.IsNullOrWhiteSpace(editor.Text) || editor.SelectionLength == editor.Text.Length))
        {
            e.Handled = true;
            if (!e.IsRepeat && GetItem(editor) is ChecklistItem datedItem && TryCommitDueDate(datedItem))
                VisualTreeHelpers.FindAncestor<DatePicker>(editor)?.SetCurrentValue(DatePicker.IsDropDownOpenProperty, true);
            return;
        }
        var opensCalendar = e.Key == Key.System && e.SystemKey == Key.Down &&
            e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);
        if ((e.Key == Key.Enter || opensCalendar) &&
            GetItem(sender as DependencyObject) is { } item && !TryCommitDueDate(item))
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
        var current = _creatingItem ?? _editingItem;
        if (current is not null && !TryFinishItem(current, keepEmpty: true, focusList: false))
            return;

        var item = _viewModel.AddItem(anchor);
        if (item is null) return;
        _creatingItem = item;
        _creationPriority = item.Priority;
        _creationTabId = item.TabId;
        item.IsEditingTitle = true;
        _editingItem = item;
        _viewModel.DeferContentRefresh(true);
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_creatingItem, item) && _viewModel.Items.Contains(item))
                FocusTitle(item, selectAll: false);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool IsEmptyCreation(ChecklistItem item) =>
        ReferenceEquals(item, _creatingItem) &&
        string.IsNullOrWhiteSpace(item.Text) && string.IsNullOrWhiteSpace(item.Notes) &&
        item.DueDate is null && !item.IsChecked &&
        item.Priority == _creationPriority && item.TabId == _creationTabId;

    private bool TryFinishItem(ChecklistItem item, bool keepEmpty, bool focusList, bool collapse = true)
    {
        if (_completingItem) return false;
        // warning dialogs can dispatch focus changes before this completion has returned.
        _completingItem = true;
        try
        {
            var destination = CaptureFocus();
            if (destination is not null && ReferenceEquals(destination.Item, item) && IsEditor(destination.Element))
                _lastEditorFocus = destination;
            var index = _viewModel.FilteredItems.IndexOf(item);
            if (!_viewModel.Items.Contains(item))
            {
                if (!TryFlushPendingContent(out var pendingError))
                {
                    ShowPersistenceWarningOnce(pendingError);
                    return false;
                }
                if (ReferenceEquals(_creatingItem, item)) _creatingItem = null;
                if (ReferenceEquals(_editingItem, item)) _editingItem = null;
                item.IsEditingTitle = false;
                _viewModel.DeferContentRefresh(false);
                return true;
            }

            if (!TryCommitDueDate(item))
            {
                FocusDate(item);
                return false;
            }

            var remove = !keepEmpty && IsEmptyCreation(item);
            if (!_viewModel.TrySaveItems(remove ? item : null, out var error))
            {
                ShowPersistenceWarningOnce(error);
                RestoreEditorFocus(item);
                return false;
            }
            // content entered during a save notification must not be removed as an empty creation.
            if (remove && !IsEmptyCreation(item))
            {
                if (!_viewModel.TrySaveItems(null, out error))
                {
                    ShowPersistenceWarningOnce(error);
                    RestoreEditorFocus(item);
                    return false;
                }
                remove = false;
            }

            if (ReferenceEquals(_creatingItem, item)) _creatingItem = null;
            if (ReferenceEquals(_editingItem, item)) _editingItem = null;
            item.IsEditingTitle = false;
            if (collapse) item.IsExpanded = false;
            if (remove) _viewModel.RemoveItem(item);
            _viewModel.DeferContentRefresh(false);
            if (focusList)
            {
                FocusRow(item, index);
            }
            else if (destination is not null && !ReferenceEquals(destination.Item, item)) RestoreFocus(destination);
            else if (destination?.Item is not null) FocusRow(item, index);
            if (Keyboard.FocusedElement is DependencyObject focused && IsEditor(focused) &&
                GetItem(focused) is ChecklistItem next && !ReferenceEquals(next, item))
            {
                _editingItem = next;
                _viewModel.DeferContentRefresh(true);
                _lastEditorFocus = CaptureFocus();
            }
            return true;
        }
        finally { _completingItem = false; }
    }

    private void NavigateItem(ChecklistItem item, UIElement? source, bool backward)
    {
        if (source is null) return;
        _navigatingItem = true;
        try
        {
            if (!backward && source is TextBox { Name: "ItemTitleTextBox" })
            {
                item.IsExpanded = true;
                ItemsList.UpdateLayout();
                FindItemControl<TextBox>(item, "NotesTextBox")?.Focus();
            }
            else if (backward && source is TextBox { Name: "NotesTextBox" } && item.IsEditingTitle)
            {
                var title = FindItemControl<TextBox>(item, "ItemTitleTextBox");
                if (title is not null && title.Focus())
                {
                    if (ReferenceEquals(_titleSelectionItem, item))
                        RestoreSelection(title, _titleSelectionStart, _titleSelectionLength);
                    else
                        title.CaretIndex = title.Text.Length;
                }
            }
            else
            {
                var controls = GetTabControls(item);
                var index = controls.FindIndex(control => ReferenceEquals(control, source));
                if (index < 0)
                    index = controls.FindIndex(control => control is not ListBoxItem && IsWithin(source, control));
                var next = index + (backward ? -1 : 1);
                if (next >= 0 && next < controls.Count) controls[next].Focus();
                else MoveOutsideList(backward);
            }
        }
        finally { _navigatingItem = false; }

        var destination = Keyboard.FocusedElement as FrameworkElement;
        if (destination is null || ReferenceEquals(GetItem(destination), item)) return;
        FinishNavigation(item);
    }

    private List<UIElement> GetTabControls(ChecklistItem item)
    {
        var controls = new List<UIElement>();
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row) controls.Add(row);
        foreach (var name in new[] { "ItemDetailsButton", "ItemCompletionCheckBox", "ItemTitleTextBox", "NotesTextBox", "ItemDueDatePicker", "ClearItemDueDateButton" })
        {
            var control = FindItemControl<UIElement>(item, name);
            if (control is DatePicker picker)
            {
                var editor = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker);
                var button = VisualTreeHelpers.FindDescendant<Button>(picker, "PART_Button");
                if (editor is { IsVisible: true, IsEnabled: true, Focusable: true }) controls.Add(editor);
                if (button is { IsVisible: true, IsEnabled: true, Focusable: true }) controls.Add(button);
            }
            else if (control is { IsVisible: true, IsEnabled: true, Focusable: true }) controls.Add(control);
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

    private void FinishNavigation(ChecklistItem item)
    {
        // keep the clicked title in place until mouse-up resolves the editing handoff.
        if (_headerClickItem is not null && _headerClickEditsTitle) return;
        if (!ReferenceEquals(item, _creatingItem) && !ReferenceEquals(item, _editingItem)) return;
        TryFinishItem(item, keepEmpty: false, focusList: false);
    }

    private void Item_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (GetItem(e.NewFocus as DependencyObject) is not ChecklistItem item) return;
        ItemsList.SelectedItem = item;
        if (_completingItem || !IsEditor(e.NewFocus as DependencyObject)) return;
        _editingItem = item;
        _viewModel.DeferContentRefresh(true);
        _lastEditorFocus = CaptureFocus();
    }

    private static bool IsEditor(DependencyObject? element) =>
        element is TextBox { Name: "ItemTitleTextBox" or "NotesTextBox" } ||
        VisualTreeHelpers.FindAncestor<DatePicker>(element) is not null;

    private void Item_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_completingItem || !IsEditor(e.NewFocus as DependencyObject) ||
            GetItem(e.NewFocus as DependencyObject) is not ChecklistItem item) return;
        var previous = _creatingItem ?? _editingItem;
        if (previous is null || ReferenceEquals(previous, item)) return;
        var destination = CaptureFocus(e.NewFocus as FrameworkElement);
        if (!TryFinishItem(previous, keepEmpty: false, focusList: false))
        {
            e.Handled = true;
            return;
        }
        if (!ReferenceEquals(GetItem(e.NewFocus as DependencyObject), item))
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (destination is not null && _viewModel.Items.Contains(item)) RestoreFocus(destination);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void Item_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_completingItem || GetItem(e.OldFocus as DependencyObject) is not ChecklistItem item) return;
        if (IsEditor(e.OldFocus as DependencyObject)) _lastEditorFocus = CaptureFocus(e.OldFocus as FrameworkElement);
        if (e.OldFocus is DatePickerTextBox && !TryCommitDueDate(item)) e.Handled = true;
    }

    private void Item_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_navigatingItem || _completingItem || sender is not FrameworkElement { DataContext: ChecklistItem item })
            return;
        if (ReferenceEquals(GetItem(e.NewFocus as DependencyObject), item) || IsItemPopupOpen(item)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed || !ReferenceEquals(_editingItem, item) || _navigatingItem || _completingItem || IsItemPopupOpen(item) ||
                ReferenceEquals(GetItem(Keyboard.FocusedElement as DependencyObject), item)) return;
            FinishNavigation(item);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BeginTitleEditing(ChecklistItem item, Point? clickPosition = null)
    {
        var previous = _creatingItem ?? _editingItem;
        if (previous is not null && !ReferenceEquals(previous, item) &&
            !TryFinishItem(previous, keepEmpty: false, focusList: false)) return;
        if (!_viewModel.FilteredItems.Contains(item)) return;
        _editingItem = item;
        item.IsEditingTitle = true;
        _viewModel.DeferContentRefresh(true);
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
        else editor.CaretIndex = editor.Text.Length;
    }

    private void FocusDate(ChecklistItem item)
    {
        item.IsExpanded = true;
        ItemsList.ScrollIntoView(item);
        ItemsList.UpdateLayout();
        VisualTreeHelpers.FindDescendant<DatePickerTextBox>(FindItemControl<DatePicker>(item, "ItemDueDatePicker"))?.Focus();
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
        if (e.NewFocus is ListBoxItem row && row.DataContext is ChecklistItem selected)
            ItemsList.SelectedItem = selected;
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
            VisualTreeHelpers.FindAncestor<DatePicker>(element) is not null ? "ItemDueDatePicker" : element.Name,
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
        if (control is DatePicker picker) control = VisualTreeHelpers.FindDescendant<DatePickerTextBox>(picker);
        if (control?.IsVisible == true && control.Focus())
        {
            if (control is TextBox editor) RestoreSelection(editor, target.Start, target.Length);
        }
        else FocusRow(target.Item);
    }

    private void ViewModel_ViewChanging(object? sender, CancelEventArgs e)
    {
        var item = _creatingItem ?? _editingItem;
        if (_completingItem || item is null) return;
        if (!TryFinishItem(item, keepEmpty: false, focusList: false))
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(),
                System.Windows.Threading.DispatcherPriority.Input);
        }
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
            if (IsVisible && version == _interactionVersion &&
                (focused is null || ReferenceEquals(focused, target.Element) || ReferenceEquals(focused, ItemsList)))
                RestoreFocus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var active = _creatingItem ?? _editingItem;
        if (active is null || _viewModel.Items.Contains(active)) return;
        active.IsEditingTitle = false;
        _creatingItem = null;
        _editingItem = null;
        _viewModel.DeferContentRefresh(false);
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

    private static void RestoreSelection(TextBox editor, int start, int length)
    {
        start = Math.Min(start, editor.Text.Length);
        editor.Select(start, Math.Min(length, editor.Text.Length - start));
    }

    private bool IsItemPopupOpen(ChecklistItem item) =>
        FindItemControl<DatePicker>(item, "ItemDueDatePicker")?.IsDropDownOpen == true ||
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
            if (ReferenceEquals(item, _creatingItem))
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

    private void ItemTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            var item = textBox.DataContext;
            _titleSelectionItem = textBox.DataContext as ChecklistItem;
            _titleSelectionStart = textBox.SelectionStart;
            _titleSelectionLength = textBox.SelectionLength;
            Dispatcher.BeginInvoke(() =>
            {
                if (textBox.IsKeyboardFocused || !textBox.IsVisible || !ReferenceEquals(textBox.DataContext, item)) return;
                textBox.Select(0, 0);
                textBox.ScrollToHorizontalOffset(0);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
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
