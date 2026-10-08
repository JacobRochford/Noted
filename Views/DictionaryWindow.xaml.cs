using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class DictionaryWindow : OverlayWindow
{
    private readonly IAppSettingsService _settingsService;
    private readonly DictionaryWindowViewModel _viewModel;
    private string? _lastPersistenceWarning;
    private bool _reopenOnStartup;
    private bool _preserveOpenStateOnClose;

    public DictionaryWindow(
        IAppSettingsService settingsService,
        IDictionaryContentService contentService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        var windowState = _settingsService.LoadDictionaryWindowState();
        _reopenOnStartup = windowState.ReopenOnStartup;
        RestoreWindowBounds(
            windowState.Left,
            windowState.Top,
            windowState.Width,
            windowState.Height);

        _viewModel = new DictionaryWindowViewModel(
            contentService,
            Dispatcher,
            action => Dispatcher.Invoke(action),
            windowState.GhostModeEnabled);
        DataContext = _viewModel;
        InitializeOverlay(windowState.GhostModeEnabled, windowState.GhostModeOpacity, windowState.Opacity);

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DictionaryWindowViewModel.GhostModeEnabled))
            {
                ApplyGhostMode(_viewModel.GhostModeEnabled);
                SaveWindowState();
            }
            else if (e.PropertyName == nameof(DictionaryWindowViewModel.PersistenceError) &&
                     !_viewModel.HasPersistenceError)
            {
                _lastPersistenceWarning = null;
            }
        };
        IsVisibleChanged += DictionaryWindow_IsVisibleChanged;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        var clickedItem = VisualTreeHelpers.FindAncestor<FrameworkElement>(e.OriginalSource as DependencyObject)?.DataContext as DictionaryItem;
        var finishedNewEntries = FinishNewEntries(except: clickedItem);
        if (Keyboard.FocusedElement is TextBox textBox &&
            !ReferenceEquals(
                VisualTreeHelpers.FindAncestor<TextBox>(e.OriginalSource as DependencyObject),
                textBox))
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ItemsList.Focus();
        }

        if (finishedNewEntries)
            _viewModel.TryFlushPendingItems(out _);

        base.OnPreviewMouseDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        if (_viewModel.Items.Any(item => item.IsAutoSaving))
            _viewModel.TryFlushPendingItems(out _);

        base.OnDeactivated(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            AddEntry();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control &&
            VisualTreeHelpers.FindAncestor<FrameworkElement>(Keyboard.FocusedElement as DependencyObject)?.DataContext
                is DictionaryItem { IsEditing: true, IsAutoSaving: false } item)
        {
            SaveEntry(item);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchTextBox.IsKeyboardFocusWithin)
        {
            if (SearchTextBox.Text.Length > 0)
                SearchTextBox.Clear();
            else
                ItemsList.Focus();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    internal bool TryFlushPendingContent(out string? error)
    {
        FinishNewEntries();
        if (_viewModel.Items.Any(item => item.HasPendingEdit))
        {
            error = "Save or cancel the dictionary entry being edited first.";
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
        IsVisibleChanged -= DictionaryWindow_IsVisibleChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddEntry();

    private void AddEntry()
    {
        if (FinishNewEntries())
            _viewModel.TryFlushPendingItems(out _);

        var item = _viewModel.AddItem(beginEdit: true, autoSave: true);
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                ItemsList.UpdateLayout();
                if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container)
                    return;

                var wordBox = VisualTreeHelpers.FindDescendant<TextBox>(container, "WordTextBox");
                wordBox?.BringIntoView();
                wordBox?.Focus();
                wordBox?.SelectAll();
                ItemsList.UpdateLayout();
                (container as FrameworkElement)?.BringIntoView();
            }));
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    protected override void RequestHide()
    {
        if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return;
        }

        ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        HideWindow();
    }

    private void DictionaryWindow_IsVisibleChanged(
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
            "Dictionary Save Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void DeleteWordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DictionaryItem item)
        {
            _viewModel.RemoveItem(item);
        }
    }

    private void DictionaryItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } contextMenu } itemRoot)
            return;

        contextMenu.PlacementTarget = itemRoot;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private bool FinishNewEntries(DictionaryItem? except = null)
    {
        var finished = false;
        foreach (var item in _viewModel.Items.Where(item => item.IsAutoSaving && !ReferenceEquals(item, except)).ToList())
        {
            var empty = string.IsNullOrWhiteSpace(item.Word) && string.IsNullOrWhiteSpace(item.Definition);
            item.FinishAutoSave();
            finished = true;
            if (empty) _viewModel.RemoveItem(item);
        }
        return finished;
    }

    private void Definition_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None ||
            sender is not DependencyObject definition)
            return;

        var scroll = VisualTreeHelpers.FindDescendant<ScrollViewer>(definition);
        if (scroll is null)
            return;

        var canScroll = e.Delta > 0
            ? scroll.VerticalOffset > 0
            : scroll.VerticalOffset < scroll.ScrollableHeight;
        if (canScroll)
            return;

        // Bypass the inner viewer, which otherwise consumes wheel input at its boundaries.
        e.Handled = true;
        ItemsScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
    }

    private void ViewDefinition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item })
            return;

        if (item.IsEditing)
            return;

        item.IsExpanded = !item.IsExpanded;
        if (!item.IsExpanded)
            return;

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (!item.IsExpanded)
                    return;

                ItemsList.UpdateLayout();
                (ItemsList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement)?.BringIntoView();
            }));
    }

    private void WordHeader_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        EditDefinitionButton_Click(sender, e);
        e.Handled = true;
    }

    private void EditDefinitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item }) return;
        item.FinishAutoSave();
        item.BeginEdit();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (!item.IsEditing)
                    return;

                ItemsList.UpdateLayout();
                if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                    return;

                var wordBox = VisualTreeHelpers.FindDescendant<TextBox>(container, "WordTextBox");
                wordBox?.Focus();
                wordBox?.SelectAll();
                ItemsList.UpdateLayout();
                container.BringIntoView();
                // keep Save/Cancel visible
                VisualTreeHelpers.FindDescendant<StackPanel>(container, "EditActionsPanel")?.BringIntoView();
            }));
    }

    private void SaveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item }) return;
        SaveEntry(item);
    }

    private void SaveEntry(DictionaryItem item)
    {
        item.SaveEdit();
        if (!_viewModel.TryFlushPendingItems(out _))
            item.BeginEdit();
    }

    private void CancelEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item }) return;
        item.CancelEdit();
        if (item.IsNew) _viewModel.RemoveItem(item);
    }

    private void WordTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            if (textBox.DataContext is DictionaryItem { IsAutoSaving: true })
                return;

            textBox.Select(0, 0);
            textBox.ScrollToHorizontalOffset(0);
        }
    }

    protected override void SaveWindowState()
    {
        var newState = new DictionaryWindowState
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            Opacity = _defaultOpacity,
            GhostModeOpacity = _ghostModeOpacity,
            GhostModeEnabled = _ghostModeEnabled,
            ReopenOnStartup = _reopenOnStartup
        };
        _settingsService.SaveDictionaryWindowState(newState);
    }

    internal void CancelPreparedClose() => _preserveOpenStateOnClose = false;

    internal void PrepareForApplicationShutdown()
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        SaveWindowState();
        _preserveOpenStateOnClose = true;
    }

}
