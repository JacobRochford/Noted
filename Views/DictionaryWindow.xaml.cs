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
    private readonly Dictionary<DictionaryItem, double> _definitionHeights = [];
    private string? _lastPersistenceWarning;
    private bool _reopenOnStartup;
    private bool _preserveOpenStateOnClose;
    private bool _completingEntry;
    private bool _isClosed;
    private bool _isResettingSize;
    private FocusTarget? _lastEditorFocus;
    private readonly ItemEditorSelections<DictionaryItem> _editorSelections = new();
    private DictionaryItem? _commandItem;

    private sealed record FocusTarget(
        FrameworkElement Element, DictionaryItem? Item, string Name, int Start, int Length);

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
        if (windowState.IsMaximized)
            WindowState = WindowState.Maximized;

        _viewModel = new DictionaryWindowViewModel(
            contentService,
            Dispatcher,
            action => Dispatcher.Invoke(action),
            windowState.GhostModeEnabled,
            new DictionaryDraftStore(settingsService.AppDataDirectory));
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

    protected override void OnDeactivated(EventArgs e)
    {
        if (!_completingEntry)
            _viewModel.TryFlushPendingItems(out _);

        base.OnDeactivated(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && e.IsRepeat) e.Handled = true;
        var item = GetItem(e.OriginalSource as DependencyObject);
        var modifiers = e.KeyboardDevice.Modifiers;
        if (e.Handled || _completingEntry || _viewModel.Items.Any(IsEntryPopupOpen))
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        if (e.Key == Key.S && modifiers == ModifierKeys.Control)
        {
            if (!e.IsRepeat) SaveCurrentEntry(item ?? _commandItem);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && modifiers == ModifierKeys.None && item is not null)
        {
            if (!e.IsRepeat && !item.IsEditing) BeginEntryEditing(item);
            e.Handled = true;
        }
        else if (e.Key == Key.N && modifiers == ModifierKeys.Control)
        {
            if (!e.IsRepeat) AddEntry();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control &&
                 (item ?? _commandItem) is { IsEditing: true } edit)
        {
            TrySaveEntry(edit, collapse: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && item?.IsEditing == true &&
            e.OriginalSource is TextBox editor &&
            (modifiers == ModifierKeys.None && editor.Name == "WordTextBox" ||
             modifiers == ModifierKeys.Shift && editor.Name == "DefinitionBox"))
        {
            if (modifiers == ModifierKeys.None)
            {
                RememberEditorFocus();
                FindEntryControl<TextBox>(item, "DefinitionBox")?.Focus();
            }
            else
            {
                FindEntryControl<TextBox>(item, "WordTextBox")?.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F && modifiers.HasFlag(ModifierKeys.Control))
        {
            FocusSearch();
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            !_viewModel.Items.Any(IsEntryPopupOpen) && !_completingEntry)
        {
            var item = GetItem(e.OriginalSource as DependencyObject) ?? _commandItem;
            if (item?.IsEditing == true && _viewModel.Items.Contains(item))
            {
                if (CancelEntry(item)) FocusEntry(item);
            }
            else RequestHide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void DefinitionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled && sender is TextBox editor)
            e.Handled = EditorTextWrapping.HandleKey(editor, e.Key, e.KeyboardDevice.Modifiers);
    }

    private void DefinitionContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid content && content.DataContext is DictionaryItem item &&
            _definitionHeights.TryGetValue(item, out var height))
        {
            content.MaxHeight = double.PositiveInfinity;
            content.Height = height;
        }
    }

    private void DefinitionResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb grip || grip.Tag is not Grid content ||
            content.DataContext is not DictionaryItem item ||
            e.HorizontalChange == 0 && e.VerticalChange == 0)
            return;

        if (e.HorizontalChange != 0)
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);

        if (e.VerticalChange != 0)
        {
            var currentHeight = double.IsNaN(content.Height) ? content.ActualHeight : content.Height;
            var previousHeight = Height;
            var verticalChange = Math.Max(e.VerticalChange, Math.Min(0, 48 - currentHeight));
            var windowHeight = Math.Clamp(previousHeight + verticalChange, MinHeight, MaxHeight);
            Height = windowHeight;

            // leave room for the word, definition heading, and editing actions.
            var maximumHeight = Math.Max(48, ItemsScrollViewer.ViewportHeight + windowHeight - previousHeight - 140);
            // use the requested size so native window rounding does not change the definition height.
            var height = Math.Clamp(currentHeight + windowHeight - previousHeight, 48, maximumHeight);
            _definitionHeights[item] = height;
            content.MaxHeight = double.PositiveInfinity;
            content.Height = height;

            ItemsScrollViewer.UpdateLayout();
            grip.BringIntoView();
        }

        e.Handled = true;
    }

    private void DefinitionResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveWindowState();

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
        _isClosed = true;
        IsVisibleChanged -= DictionaryWindow_IsVisibleChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddEntry();

    private void AddEntry()
    {
        BeginEntryEditing(_viewModel.AddItem(beginEdit: true));
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => RequestHide();

    private void FindMenuItem_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() => FocusSearch(), System.Windows.Threading.DispatcherPriority.Input);

    private void FocusSearch()
    {
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!WindowDragHitTest.CanDrag(e.OriginalSource as DependencyObject, TitleBar))
            return;

        var menu = (ContextMenu)TitleBar.FindResource("TitleBarMenu");
        menu.PlacementTarget = TitleBar;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ResetSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _isResettingSize = true;
        try
        {
            WindowState = WindowState.Normal;
            var defaults = new DictionaryWindowState();
            RestoreWindowBounds(Left, Top, defaults.Width, defaults.Height);
        }
        finally
        {
            _isResettingSize = false;
        }
        SaveWindowState();
    }

    protected override void RequestHide()
    {
        TryHide();
    }

    internal bool TryHide()
    {
        if (_completingEntry || !CheckHideDrafts()) return false;
        if (!TryFlushPendingContent(out var error))
        {
            ShowPersistenceWarningOnce(error);
            return false;
        }
        if (!CheckHideDrafts()) return false;
        ItemsList.Focus();
        ShowPersistenceWarningOnce(_viewModel.PersistenceError);
        HideWindow();
        return true;
    }

    private bool CheckHideDrafts()
    {
        var pending = _viewModel.Items.FirstOrDefault(item => item.HasPendingEdit);
        if (pending is not null)
        {
            ShowPersistenceWarningOnce("Save or cancel the dictionary entry being edited first.");
            RestoreEditorFocus(pending);
            return false;
        }
        return true;
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
            if (item.IsEditing && !CancelEntry(item)) return;
            _definitionHeights.Remove(item);
            ForgetEditor(item);
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

    private bool TrySaveEntry(DictionaryItem item, bool collapse)
    {
        if (_completingEntry) return false;
        _completingEntry = true;
        try
        {
            RememberEditorFocus();
            if (!_viewModel.TrySaveEntry(item, out _) || item.IsEditing)
            {
                RestoreEditorFocus(item);
                return false;
            }
            if (collapse) item.IsExpanded = false;
            ForgetEditor(item);
            FocusEntry(item);
            return true;
        }
        finally { _completingEntry = false; }
    }

    private static DictionaryItem? GetItem(DependencyObject? target)
    {
        for (var element = VisualTreeHelpers.FindAncestor<FrameworkElement>(target); element is not null;
             element = VisualTreeHelpers.FindAncestor<FrameworkElement>(VisualTreeHelper.GetParent(element)))
        {
            if (element.DataContext is DictionaryItem item) return item;
            if (element is ContextMenu menu) return (menu.PlacementTarget as FrameworkElement)?.DataContext as DictionaryItem;
        }
        return null;
    }

    private T? FindEntryControl<T>(DictionaryItem item, string name) where T : DependencyObject =>
        VisualTreeHelpers.FindDescendant<T>(ItemsList.ItemContainerGenerator.ContainerFromItem(item), name);

    private bool IsEntryPopupOpen(DictionaryItem item) =>
        FindEntryControl<Border>(item, "DictionaryItemRoot")?.ContextMenu?.IsOpen == true;

    private FocusTarget? CaptureFocus(FrameworkElement? element = null)
    {
        element ??= Keyboard.FocusedElement as FrameworkElement;
        if (element is null) return null;
        return new FocusTarget(element, GetItem(element), element.Name,
            (element as TextBox)?.SelectionStart ?? 0, (element as TextBox)?.SelectionLength ?? 0);
    }

    private void RememberEditorFocus()
    {
        var target = CaptureFocus();
        if (target?.Item is not null && target.Element is TextBox { Name: "WordTextBox" or "DefinitionBox" })
            _lastEditorFocus = target;
    }

    private void DictionaryItem_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_completingEntry && e.OldFocus is TextBox editor &&
            editor.DataContext is DictionaryItem { IsEditing: true } item)
        {
            _editorSelections.Remember(item, editor);
            _lastEditorFocus = CaptureFocus(editor);
        }
    }

    private void DictionaryItem_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item }) return;
        _commandItem = item;
        if (e.NewFocus is TextBox editor) _editorSelections.Restore(item, editor);
    }

    private void ForgetEditor(DictionaryItem item)
    {
        _editorSelections.Forget(item);
        if (ReferenceEquals(_lastEditorFocus?.Item, item)) _lastEditorFocus = null;
        if (ReferenceEquals(_commandItem, item)) _commandItem = null;
    }

    private void RestoreEditorFocus(DictionaryItem item)
    {
        if (!_viewModel.Items.Contains(item)) return;
        if (!_viewModel.FilteredItems.Contains(item)) _viewModel.SearchText = "";
        item.IsExpanded = true;
        ItemsList.UpdateLayout();
        if (_lastEditorFocus is { } target && ReferenceEquals(target.Item, item)) RestoreFocus(target);
        else FindEntryControl<TextBox>(item, "WordTextBox")?.Focus();
    }

    private void RestoreFocus(FocusTarget target)
    {
        if (target.Item is null || VisualTreeHelpers.FindAncestor<ContextMenu>(target.Element)?.IsOpen == true)
        {
            if (target.Element.IsVisible) target.Element.Focus();
            return;
        }
        ItemsList.UpdateLayout();
        var control = FindEntryControl<FrameworkElement>(target.Item, target.Name);
        if (control?.IsVisible != true || !control.Focus())
        {
            FocusEntry(target.Item);
            return;
        }
        if (control is TextBox editor)
            ItemEditorSelections<DictionaryItem>.Apply(editor, target.Start, target.Length);
    }

    private void FocusEntry(DictionaryItem? item)
    {
        ItemsList.UpdateLayout();
        if (item is not null && FindEntryControl<Button>(item, "WordHeaderButton") is { IsVisible: true } header)
            header.Focus();
        else ItemsList.Focus();
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

        e.Handled = true;
        if (scroll.ScrollableHeight > 0)
            return;

        // let the list scroll when the entire definition fits.
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

        _commandItem = item;
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

    private void ExpandedItemBackground_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem { IsExpanded: true } item }) return;
        _commandItem = item;
        if (item.IsEditing) FindEntryControl<Border>(item, "DictionaryItemRoot")?.Focus();
        else item.IsExpanded = false;
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
        if (item.IsEditing)
        {
            SaveEntry(item);
            return;
        }

        BeginEntryEditing(item);
    }

    private void BeginEntryEditing(DictionaryItem item)
    {
        item.BeginEdit();
        _commandItem = item;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (_isClosed || !IsVisible || !item.IsEditing || !_viewModel.Items.Contains(item))
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
        TrySaveEntry(item, collapse: false);
    }

    private void SaveMenuItem_Click(object sender, RoutedEventArgs e) => SaveCurrentEntry(_commandItem);

    private void SaveCurrentEntry(DictionaryItem? item)
    {
        if (_completingEntry) return;
        if (item?.IsEditing == true && _viewModel.Items.Contains(item))
            SaveEntry(item);
        else if (!TryFlushPendingContent(out var error))
            ShowPersistenceWarningOnce(error);
    }

    private void CancelEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item }) return;
        CancelEntry(item);
    }

    private bool CancelEntry(DictionaryItem item)
    {
        if (!_viewModel.TryCancelEntry(item, out var error))
        {
            ShowPersistenceWarningOnce(error);
            RestoreEditorFocus(item);
            return false;
        }
        if (item.IsNew) _definitionHeights.Remove(item);
        ForgetEditor(item);
        return true;
    }

    protected override void SaveWindowState()
    {
        var bounds = GetNormalWindowBounds();
        var newState = new DictionaryWindowState
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            Opacity = _defaultOpacity,
            GhostModeOpacity = _ghostModeOpacity,
            GhostModeEnabled = _ghostModeEnabled,
            ReopenOnStartup = _reopenOnStartup
        };
        _settingsService.SaveDictionaryWindowState(newState);
    }

    protected override bool ShouldSaveWindowStateAfterStateChange => !_isResettingSize;

    internal void CancelPreparedClose() => _preserveOpenStateOnClose = false;

    internal void PrepareForApplicationShutdown()
    {
        _reopenOnStartup = IsWindowVisible || IsHiddenTogether;
        SaveWindowState();
        _preserveOpenStateOnClose = true;
    }

}
