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
    private ChecklistItem? _draggedItem;
    private string? _lastPersistenceWarning;

    public ChecklistWindow(
        IAppSettingsService settingsService,
        IChecklistContentService contentService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        var state = _settingsService.LoadChecklistWindowState();
        RestoreWindowBounds(state.Left, state.Top, state.Width, state.Height);

        _viewModel = new ChecklistWindowViewModel(
            contentService,
            Dispatcher,
            action => Dispatcher.Invoke(action),
            state.GhostModeEnabled);
        DataContext = _viewModel;
        InitializeOverlay(state.GhostModeEnabled, state.GhostModeOpacity, state.Opacity);

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ChecklistWindowViewModel.GhostModeEnabled))
            {
                ApplyGhostMode(_viewModel.GhostModeEnabled);
                SaveWindowState();
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!TryFlushPendingContent(out var error))
        {
            e.Cancel = true;
            ShowPersistenceWarningOnce(error);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsVisibleChanged -= ChecklistWindow_IsVisibleChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnTitleBarDoubleClick() => ToggleWindow();

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
            GhostModeEnabled = _ghostModeEnabled
        });
    }

    // ── Title bar ─────────────────────────────────────────────────────────

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    private void RequestHide()
    {
        if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return;
        }

        ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        HideWindow();
    }

    private void ChecklistWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
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

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.IsSearchVisible = false;
            _viewModel.SearchQuery = "";
            e.Handled = true;
        }
    }

    // ── Add / delete ──────────────────────────────────────────────────────

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.AddItem();
        Dispatcher.BeginInvoke(() => FocusItem(item),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var item = sender is MenuItem menuItem
            ? GetItemFromContextMenu(menuItem)
            : GetItemFromSender(sender);
        if (item != null) _viewModel.RemoveItem(item);
    }

    // ── Expand / collapse ─────────────────────────────────────────────────

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

    // ── Due date ──────────────────────────────────────────────────────────

    private void ClearDueDate_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromSender(sender);
        if (item != null) item.DueDate = null;
    }

    // ── Priority ──────────────────────────────────────────────────────────

    private void PriorityStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

    // ── Context menu reorder / duplicate ──────────────────────────────────

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
        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>()
                     .Where(item => Equals(item.Tag, "ManualReorder")))
        {
            menuItem.IsEnabled = _viewModel.CanManuallyReorder;
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItemFromContextMenu(sender as MenuItem);
        if (item != null) _viewModel.DuplicateItem(item);
    }

    // ── Bulk actions ──────────────────────────────────────────────────────

    private void ClearCompleted_Click(object sender, RoutedEventArgs e) => _viewModel.ClearCompleted();
    private void CheckAll_Click(object sender, RoutedEventArgs e)       => _viewModel.CheckAll();
    private void UncheckAll_Click(object sender, RoutedEventArgs e)     => _viewModel.UncheckAll();

    // ── Keyboard navigation ───────────────────────────────────────────────

    private void ItemTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChecklistItem item) return;

        if (e.Key == Key.Enter)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            var newItem = _viewModel.InsertItemAfter(item);
            Dispatcher.BeginInvoke(() => FocusItem(newItem),
                System.Windows.Threading.DispatcherPriority.Input);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && string.IsNullOrEmpty(tb.Text))
        {
            var idx = _viewModel.FilteredItems.IndexOf(item);
            _viewModel.RemoveItem(item);
            Dispatcher.BeginInvoke(() => FocusItemAt(Math.Max(0, idx - 1)),
                System.Windows.Threading.DispatcherPriority.Input);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void ItemTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    private void ItemTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    // ── Drag-drop reordering ─────────────────────────────────────────────

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanManuallyReorder &&
            sender is FrameworkElement fe &&
            fe.DataContext is ChecklistItem item)
        {
            _draggedItem = item;
            DragDrop.DoDragDrop(fe, item, DragDropEffects.Move);
            _draggedItem = null;
        }
        e.Handled = true;
    }

    private void ItemsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.CanManuallyReorder && _draggedItem != null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ItemsList_Drop(object sender, DragEventArgs e)
    {
        if (!_viewModel.CanManuallyReorder || _draggedItem == null) return;
        var target = GetItemAtPoint(e.GetPosition(ItemsList));
        if (target != null && !ReferenceEquals(target, _draggedItem))
            _viewModel.MoveItem(_draggedItem, target);
        e.Handled = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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

    private ChecklistItem? GetItemAtPoint(Point point)
    {
        var element = ItemsList.InputHitTest(point) as DependencyObject;
        while (element != null)
        {
            if (element is ListBoxItem lbi) return lbi.DataContext as ChecklistItem;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void FocusItemAt(int index)
    {
        if (index < 0 || index >= _viewModel.FilteredItems.Count) return;
        FocusItem(_viewModel.FilteredItems[index]);
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
}
