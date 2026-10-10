using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class JotsWindow : OverlayWindow
{
    private readonly IAppSettingsService _settingsService;
    private readonly JotsWindowViewModel _viewModel;
    private readonly JotsWorkspace _workspace;
    private readonly bool _ownsWorkspace;
    private readonly Guid? _jotId;
    private bool _itemDeleted;
    private DispatcherTimer? _singleClickTimer;
    internal Guid? JotId => _jotId;
    private string? _lastPersistenceWarning;
    private bool _reopenOnStartup;
    private bool _preserveOpenStateOnClose;

    public JotsWindow(IAppSettingsService settingsService, IJotsContentService contentService)
        : this(settingsService, new JotsWorkspace(contentService, System.Windows.Threading.Dispatcher.CurrentDispatcher))
    {
        _ownsWorkspace = true;
    }

    internal JotsWindow(IAppSettingsService settingsService, JotsWorkspace workspace, JotItem? item = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _workspace = workspace;
        _jotId = item?.Id;
        var windowState = _jotId is { } id
            ? settingsService.LoadJotWindowState(id)
            : settingsService.LoadJotsWindowState();
        _reopenOnStartup = windowState.ReopenOnStartup;
        RestoreWindowBounds(windowState.Left, windowState.Top, windowState.Width, windowState.Height);
        _viewModel = new JotsWindowViewModel(workspace, windowState.GhostModeEnabled);
        DataContext = _viewModel;
        InitializeOverlay(windowState.GhostModeEnabled, windowState.GhostModeOpacity, windowState.Opacity);
        if (item is not null)
        {
            _viewModel.OpenItem(item);
            BackButton.Visibility = Visibility.Collapsed;
            HideButton.ToolTip = "Close";
            WindowTitleText.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new Binding("SelectedItem.DisplayTitle"));
            SetBinding(TitleProperty, new Binding("SelectedItem.DisplayTitle"));
        }
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        IsVisibleChanged += JotsWindow_IsVisibleChanged;
    }

    internal bool TryPrepareForClose()
    {
        var item = _viewModel.SelectedItem;
        if (item is { IsNew: true, HasDraftContent: false })
            _viewModel.CancelSelectedEdit();
        else if (item?.HasPendingEdit == true)
        {
            var result = AppDialog.Show(this, "Save changes to this jot before closing?", "Unsaved Jot",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
                return false;
            if (result == MessageBoxResult.Yes)
            {
                if (!_viewModel.TrySaveSelected(out var validationError))
                {
                    ShowPersistenceWarningOnce(validationError);
                    return false;
                }
            }
            else
                _viewModel.CancelSelectedEdit();
        }
        if (!_viewModel.TryFlushPendingItems(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return false;
        }
        return true;
    }

    internal bool TryFlushPendingContent(out string? error)
    {
        var selected = _viewModel.SelectedItem;
        if (selected is { IsNew: true, IsEditing: true, HasDraftContent: false })
        {
            _viewModel.CancelSelectedEdit();
        }
        else if (selected?.HasPendingEdit == true)
        {
            error = "Save or cancel the jot being edited first.";
            return false;
        }

        var success = _viewModel.TryFlushPendingItems(out error);
        if (success && !_viewModel.HasPersistenceError)
            _lastPersistenceWarning = null;
        return success;
    }

    internal string? BackupBlockingIssue => _viewModel.PersistenceError;
    internal bool RecoveryBlocksBackup => _viewModel.RecoveryIssuesFoundThisRun;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_itemDeleted && !TryPrepareForClose())
        {
            e.Cancel = true;
            return;
        }

        if (!_preserveOpenStateOnClose)
            _reopenOnStartup = false;

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsVisibleChanged -= JotsWindow_IsVisibleChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _singleClickTimer?.Stop();
        _viewModel.Dispose();
        if (_ownsWorkspace)
            _workspace.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.N)
        {
            if (_jotId is null && _viewModel.SelectedItem?.IsEditing != true)
                BeginNewJot();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.F && !_viewModel.IsDetailOpen)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                 (e.Key == Key.S || e.Key == Key.Enter) &&
                 _viewModel.SelectedItem?.IsEditing == true)
        {
            SaveCurrentJot();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_jotId is not null)
            {
                Close();
                e.Handled = true;
            }
            else if (_viewModel.IsDetailOpen)
            {
                TryReturnToList();
                e.Handled = true;
            }
            else if (SearchTextBox.IsKeyboardFocusWithin)
            {
                if (SearchTextBox.Text.Length > 0)
                    SearchTextBox.Clear();
                else
                    ItemsList.Focus();
                e.Handled = true;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void SaveWindowState()
    {
        var state = new JotsWindowState
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            Opacity = _defaultOpacity,
            GhostModeOpacity = _ghostModeOpacity,
            GhostModeEnabled = _ghostModeEnabled,
            ReopenOnStartup = _jotId is null && _reopenOnStartup
        };
        if (_jotId is { } id)
            _settingsService.SaveJotWindowState(id, state);
        else
            _settingsService.SaveJotsWindowState(state);
    }

    internal void CancelPreparedClose() => _preserveOpenStateOnClose = false;

    internal void PrepareForApplicationShutdown()
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        SaveWindowState();
        _preserveOpenStateOnClose = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_jotId is not null && e.PropertyName == nameof(JotsWindowViewModel.IsDetailOpen) && !_viewModel.IsDetailOpen)
        {
            // deletion clears the selection first so closing can't save the item again.
            _itemDeleted = true;
            Close();
            return;
        }
        if (e.PropertyName == nameof(JotsWindowViewModel.GhostModeEnabled))
        {
            ApplyGhostMode(_viewModel.GhostModeEnabled);
            SaveWindowState();
        }
        else if (e.PropertyName == nameof(JotsWindowViewModel.PersistenceError) &&
                 !_viewModel.HasPersistenceError)
        {
            _lastPersistenceWarning = null;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => BeginNewJot();

    private void BeginNewJot()
    {
        _viewModel.BeginNewItem();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                TitleTextBox.Focus();
                TitleTextBox.SelectAll();
            }));
    }

    private void JotRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement { DataContext: JotItem item })
            return;
        e.Handled = true;
        _singleClickTimer?.Stop();
        if (e.ClickCount == 2)
        {
            WindowManager.ShowJot(item.Id);
            return;
        }

        // wait for a possible double-click before opening details in this window.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WindowInterop.GetDoubleClickTime()) };
        _singleClickTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_singleClickTimer, timer) && IsWindowVisible && !_viewModel.IsDetailOpen && _workspace.Items.Contains(item))
                _viewModel.OpenItem(item);
        };
        timer.Start();
    }

    private void OpenJotMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: JotItem item })
            WindowManager.ShowJot(item.Id);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => TryReturnToList();

    private bool TryReturnToList()
    {
        var item = _viewModel.SelectedItem;
        if (item is null)
            return true;

        if (item.IsEditing)
        {
            if (item.IsNew && !item.HasDraftContent)
            {
                _viewModel.CancelSelectedEdit();
                return true;
            }

            if (item.HasPendingEdit)
            {
                var result = AppDialog.Show(
                    this,
                    "Discard the unsaved changes to this jot?",
                    "Unsaved Jot",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                    return false;
            }

            _viewModel.CancelSelectedEdit();
            if (!_viewModel.IsDetailOpen)
                return true;
        }

        _viewModel.CloseDetail();
        return true;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BeginEditSelected();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                TitleTextBox.Focus();
                TitleTextBox.SelectAll();
            }));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveCurrentJot();

    private void SaveCurrentJot()
    {
        if (!_viewModel.TrySaveSelected(out var validationError))
        {
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                AppDialog.Show(
                    this,
                    validationError,
                    "Jots",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        if (!_viewModel.TryFlushPendingItems(out var error))
            ShowPersistenceWarningOnce(error);
        else
            ShowPersistenceWarningOnce(_viewModel.PersistenceError);
    }

    private void CancelEditButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelSelectedEdit();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is { } item)
            DeleteJot(item);
    }

    private void DeleteJotMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: JotItem item })
            DeleteJot(item);
    }

    private void DeleteJot(JotItem item)
    {
        var result = AppDialog.Show(
            this,
            $"Delete '{item.DisplayTitle}'?",
            "Delete Jot",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        _viewModel.RemoveItem(item);
    }

    private void TogglePinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: JotItem item })
            _viewModel.TogglePinned(item);
    }

    private void ToggleStarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: JotItem item })
            _viewModel.ToggleStarred(item);
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e) => _viewModel.SelectAll();
    private void InboxFilter_Click(object sender, RoutedEventArgs e) => _viewModel.SelectInbox();
    private void PinnedFilter_Click(object sender, RoutedEventArgs e) => _viewModel.SelectPinned();

    private void CategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = CategoriesButton.ContextMenu;
        if (menu is null)
            return;

        menu.Items.Clear();
        if (_viewModel.Categories.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No categories yet",
                IsEnabled = false,
                Style = (Style)FindResource("NotedContextMenuItem")
            });
        }
        else
        {
            foreach (var category in _viewModel.Categories)
            {
                var item = new MenuItem
                {
                    Header = category,
                    Tag = category,
                    IsCheckable = true,
                    IsChecked = _viewModel.IsCategoryFilter &&
                        string.Equals(_viewModel.CategoryButtonText, category, StringComparison.OrdinalIgnoreCase),
                    Style = (Style)FindResource("NotedContextMenuItem")
                };
                item.Click += CategoryFilterMenuItem_Click;
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = CategoriesButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void CategoryFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string category })
            _viewModel.SelectCategory(category);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    protected override void RequestHide()
    {
        if (_jotId is not null)
        {
            Close();
            return;
        }
        if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return;
        }

        ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        HideWindow();
    }

    private void JotsWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!_preserveOpenStateOnClose && !IsChangingGroupVisibility)
        {
            _reopenOnStartup = IsWindowVisible;
            SaveWindowState();
        }

        if (!IsVisible && !_itemDeleted)
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
            "Jots Save Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
