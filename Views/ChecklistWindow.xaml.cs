using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        IsVisibleChanged -= ChecklistWindow_IsVisibleChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        var target = e.OriginalSource as DependencyObject;
        var current = _creatingItem ?? _editingItem;
        if (e.ChangedButton == MouseButton.Left &&
            !IsWithin(target, TitleBar) && !IsWithin(target, AddItemButton) &&
            current is not null && !IsItemPopupOpen(current) &&
            !ReferenceEquals(GetItem(target), current))
        {
            var creation = _creatingItem;
            // let the click reach its original control before refreshing or removing rows.
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsVisible || !_viewModel.Items.Contains(current) ||
                    !ReferenceEquals(_creatingItem, creation)) return;
                FinishNavigation(current, completeExisting: true);
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
        var modifiers = e.KeyboardDevice.Modifiers;
        var item = GetItem(e.OriginalSource as DependencyObject);
        var popupItem = item ?? _creatingItem ?? _editingItem;
        if (!e.Handled && e.Key == Key.Escape && e.IsRepeat)
            e.Handled = true;
        if (!e.Handled && !TabNamePopup.IsOpen && (popupItem is null || !IsItemPopupOpen(popupItem)))
        {
            if (e.Key == Key.N && modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) BeginNewItem();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && item is not null)
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
                if (item is not null && (e.OriginalSource is TextBox || ReferenceEquals(item, _creatingItem)))
                    TryFinishItem(item, keepEmpty: false, focusList: true);
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
        if (item != null) item.IsExpanded = !item.IsExpanded;
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
        if (item != null) item.DueDate = null;
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
        if (_creatingItem is not null && !TryFinishItem(_creatingItem, keepEmpty: true, focusList: false))
            return;

        var item = _viewModel.AddItem(anchor);
        if (item is null) return;
        _creatingItem = item;
        _creationPriority = item.Priority;
        _creationTabId = item.TabId;
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_creatingItem, item) && _viewModel.Items.Contains(item))
                FocusItem(item);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private bool IsEmptyCreation(ChecklistItem item) =>
        ReferenceEquals(item, _creatingItem) &&
        string.IsNullOrWhiteSpace(item.Text) && string.IsNullOrWhiteSpace(item.Notes) &&
        item.DueDate is null && !item.IsChecked &&
        item.Priority == _creationPriority && item.TabId == _creationTabId;

    private bool TryFinishItem(ChecklistItem item, bool keepEmpty, bool focusList)
    {
        if (!_viewModel.Items.Contains(item))
        {
            if (!TryFlushPendingContent(out var pendingError))
            {
                ShowPersistenceWarningOnce(pendingError);
                return false;
            }
            if (ReferenceEquals(_creatingItem, item)) _creatingItem = null;
            if (ReferenceEquals(_editingItem, item)) _editingItem = null;
            return true;
        }

        var remove = !keepEmpty && IsEmptyCreation(item);
        if (!_viewModel.TrySaveItems(remove ? item : null, out var error))
        {
            ShowPersistenceWarningOnce(error);
            return false;
        }
        // content entered during a save notification must not be removed as an empty creation.
        if (remove && !IsEmptyCreation(item))
        {
            if (!_viewModel.TrySaveItems(null, out error))
            {
                ShowPersistenceWarningOnce(error);
                return false;
            }
            remove = false;
        }

        _completingItem = true;
        try
        {
            if (ReferenceEquals(_creatingItem, item)) _creatingItem = null;
            if (ReferenceEquals(_editingItem, item)) _editingItem = null;
            item.IsExpanded = false;
            if (remove) _viewModel.RemoveItem(item);
            _viewModel.DeferContentRefresh(false);
            if (focusList)
            {
                if (!remove && _viewModel.FilteredItems.Contains(item)) ItemsList.SelectedItem = item;
                ItemsList.Focus();
            }
        }
        finally { _completingItem = false; }
        return true;
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
            else if (backward && source is TextBox { Name: "NotesTextBox" })
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
                source.MoveFocus(new TraversalRequest(backward ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        finally { _navigatingItem = false; }

        var destination = Keyboard.FocusedElement as FrameworkElement;
        if (destination is null || ReferenceEquals(GetItem(destination), item)) return;
        FinishNavigation(item, completeExisting: false);
    }

    private void FinishNavigation(ChecklistItem item, bool completeExisting)
    {
        var destination = Keyboard.FocusedElement as FrameworkElement;
        var destinationItem = GetItem(destination);
        var destinationName = destination?.Name ?? "";
        var selectionStart = (destination as TextBox)?.SelectionStart ?? 0;
        var selectionLength = (destination as TextBox)?.SelectionLength ?? 0;
        if ((completeExisting || ReferenceEquals(_creatingItem, item)) &&
            !TryFinishItem(item, keepEmpty: false, focusList: false))
        {
            FocusItem(item);
            return;
        }
        _viewModel.DeferContentRefresh(false);
        if (ReferenceEquals(destinationItem, item))
        {
            ItemsList.Focus();
            return;
        }
        if (destinationItem is not null &&
            (destination?.IsKeyboardFocused != true || !ReferenceEquals(GetItem(destination), destinationItem)))
        {
            ItemsList.UpdateLayout();
            var target = destination is ListBoxItem
                ? ItemsList.ItemContainerGenerator.ContainerFromItem(destinationItem) as FrameworkElement
                : FindItemControl<FrameworkElement>(destinationItem, destinationName);
            if (target?.Focus() == true && target is TextBox textBox)
                RestoreSelection(textBox, selectionStart, selectionLength);
        }
        if (destinationItem is not null) _viewModel.DeferContentRefresh(true);
    }

    private void Item_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (GetItem(e.NewFocus as DependencyObject) is not ChecklistItem item) return;
        _editingItem = item;
        _viewModel.DeferContentRefresh(true);
    }

    private void Item_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_navigatingItem || _completingItem || sender is not FrameworkElement { DataContext: ChecklistItem item })
            return;
        if (ReferenceEquals(GetItem(e.NewFocus as DependencyObject), item) || IsItemPopupOpen(item)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_editingItem, item) || _navigatingItem || _completingItem || IsItemPopupOpen(item) ||
                ReferenceEquals(GetItem(Keyboard.FocusedElement as DependencyObject), item)) return;
            _editingItem = null;
            _viewModel.DeferContentRefresh(false);
            if (GetItem(Keyboard.FocusedElement as DependencyObject) is not null)
                _viewModel.DeferContentRefresh(true);
        }, System.Windows.Threading.DispatcherPriority.Input);
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
        if (sender is TextBox textBox && textBox.DataContext is ChecklistItem item)
            _viewModel.UpdateItemText(item, textBox.Text);
    }

    private void ItemTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChecklistItem item) return;

        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            if (TryFinishItem(item, keepEmpty: true, focusList: false)) BeginNewItem(item);
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

    private void ItemTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
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
        // Only the header background starts reordering; child controls keep their mouse gestures.
        if (!_viewModel.CanManuallyReorder ||
            e.OriginalSource is not Grid { Name: "ItemDragArea", DataContext: ChecklistItem item } dragArea)
            return;

        var container = VisualTreeHelpers.FindAncestor<ListBoxItem>(dragArea);
        if (container is null) return;

        _dragStartPoint = e.GetPosition(ItemsList);
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
        => ClearPendingDrag();

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

    private void FocusItem(ChecklistItem? item)
    {
        if (item is null || !_viewModel.FilteredItems.Contains(item)) return;
        ItemsList.ScrollIntoView(item);
        ItemsList.UpdateLayout();
        var container = ItemsList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        if (container == null) return;
        var tb = VisualTreeHelpers.FindDescendant<TextBox>(container);
        if (tb == null) return;
        tb.Focus();
        tb.CaretIndex = tb.Text?.Length ?? 0;
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
