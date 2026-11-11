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
    private DictionaryItem? _wordSelectionItem;
    private int _wordSelectionStart;
    private int _wordSelectionLength;
    private DictionaryItem[] _outsideClickEntries = [];
    private int _mouseInteractionVersion;

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
        if (e.ChangedButton == MouseButton.Left)
        {
            _outsideClickEntries = [];
            _mouseInteractionVersion++;
        }
        var target = e.OriginalSource as DependencyObject;
        if (!_completingEntry && e.ChangedButton == MouseButton.Left &&
            !IsWithin(target, TitleBar) && !IsWithin(target, AddEntryButton))
        {
            var clickedItem = GetItem(target);
            _outsideClickEntries = _viewModel.Items.Where(item => item.IsAutoSaving &&
                !ReferenceEquals(item, clickedItem) && !IsEntryPopupOpen(item)).ToArray();
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            var entries = _outsideClickEntries;
            var interactionVersion = _mouseInteractionVersion;
            _outsideClickEntries = [];
            if (entries.Length > 0)
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_isClosed && IsVisible && !_completingEntry &&
                        interactionVersion == _mouseInteractionVersion && Mouse.LeftButton != MouseButtonState.Pressed)
                        TryFinishCreations(entries);
                }, System.Windows.Threading.DispatcherPriority.Input);
        }
        base.OnPreviewMouseUp(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        if (!_completingEntry && _viewModel.Items.Any(item => item.IsAutoSaving))
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
        if (e.Key == Key.N && modifiers == ModifierKeys.Control)
        {
            if (!e.IsRepeat) AddEntry();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.Control && item?.IsEditing == true)
        {
            if (item.IsAutoSaving)
            {
                TryFinishCreations([item], focusList: true);
            }
            else
            {
                TrySaveEntry(item, collapse: true);
            }
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
                var word = FindEntryControl<TextBox>(item, "WordTextBox");
                if (word?.Focus() == true)
                {
                    if (ReferenceEquals(_wordSelectionItem, item)) RestoreSelection(word, _wordSelectionStart, _wordSelectionLength);
                    else word.CaretIndex = word.Text.Length;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F && modifiers.HasFlag(ModifierKeys.Control))
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            !_viewModel.Items.Any(IsEntryPopupOpen) && !_completingEntry)
        {
            var item = GetItem(e.OriginalSource as DependencyObject);
            if (item?.IsEditing != true) item = _viewModel.Items.LastOrDefault(entry => entry.IsAutoSaving);
            if (item?.IsAutoSaving == true) TryFinishCreations([item], focusList: true);
            else if (item?.IsEditing == true)
            {
                CancelEntry(item);
                FocusEntry(item);
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
        _isClosed = true;
        IsVisibleChanged -= DictionaryWindow_IsVisibleChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => AddEntry();

    private void AddEntry()
    {
        if (!TryFinishCreations(_viewModel.Items.Where(item => item.IsAutoSaving).ToArray())) return;

        var item = _viewModel.AddItem(beginEdit: true, autoSave: true);
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (_isClosed || !IsVisible || !item.IsAutoSaving || !_viewModel.Items.Contains(item)) return;
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
        if (!TryFinishCreations(_viewModel.Items.Where(item => item.IsAutoSaving).ToArray(), hiding: true))
            return false;
        if (!CheckHideDrafts()) return false;
        foreach (var item in _viewModel.Items.Where(item => item.IsEditing))
        {
            item.CancelEdit();
            item.IsExpanded = false;
        }
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
            _definitionHeights.Remove(item);
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
            var empty = item.IsEmpty;
            item.FinishAutoSave();
            finished = true;
            if (empty) _viewModel.RemoveItem(item);
        }
        return finished;
    }

    private bool TryFinishCreations(IReadOnlyList<DictionaryItem> candidates, bool focusList = false, bool hiding = false)
    {
        if (_completingEntry) return false;
        var entries = candidates.Where(item => item.IsAutoSaving && _viewModel.Items.Contains(item)).ToArray();
        if (entries.Length == 0 && !hiding) return true;
        _completingEntry = true;
        try
        {
            var destination = CaptureFocus();
            RememberEditorFocus();
            if (!_viewModel.TryFlushCurrentItems(out var error))
            {
                ShowPersistenceWarningOnce(error);
                var active = entries.FirstOrDefault(item => ReferenceEquals(item, _lastEditorFocus?.Item)) ?? entries.FirstOrDefault();
                if (active is not null) RestoreEditorFocus(active);
                return false;
            }
            if (hiding && !CheckHideDrafts()) return false;
            foreach (var item in entries)
            {
                var empty = item.IsEmpty;
                if (empty)
                {
                    _definitionHeights.Remove(item);
                    _viewModel.RemoveSavedEmptyCreation(item);
                }
                item.FinishAutoSave();
                item.IsExpanded = false;
            }
            if (entries.Length > 0) _viewModel.FilteredItems.Refresh();
            if (focusList) FocusEntry(entries.FirstOrDefault());
            else if (destination is not null && !entries.Contains(destination.Item)) RestoreFocus(destination);
            else if (entries.Length > 0) FocusEntry(entries[0]);
            return true;
        }
        finally { _completingEntry = false; }
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

    private static bool IsWithin(DependencyObject? target, Visual ancestor) =>
        ReferenceEquals(target, ancestor) || target is Visual visual && ancestor.IsAncestorOf(visual);

    private T? FindEntryControl<T>(DictionaryItem item, string name) where T : DependencyObject =>
        VisualTreeHelpers.FindDescendant<T>(ItemsList.ItemContainerGenerator.ContainerFromItem(item), name);

    private bool IsEntryPopupOpen(DictionaryItem item) =>
        FindEntryControl<Border>(item, "DictionaryItemRoot")?.ContextMenu?.IsOpen == true;

    private FocusTarget? CaptureFocus()
    {
        if (Keyboard.FocusedElement is not FrameworkElement element) return null;
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
        if (!_completingEntry) RememberEditorFocus();
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
        if (control is TextBox editor) RestoreSelection(editor, target.Start, target.Length);
    }

    private static void RestoreSelection(TextBox editor, int start, int length)
    {
        start = Math.Min(start, editor.Text.Length);
        editor.Select(start, Math.Min(length, editor.Text.Length - start));
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
        item.BeginEdit();
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
        if (item.IsAutoSaving) TryFinishCreations([item], focusList: true);
        else TrySaveEntry(item, collapse: false);
    }

    private void CancelEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DictionaryItem item }) return;
        CancelEntry(item);
    }

    private void CancelEntry(DictionaryItem item)
    {
        item.CancelEdit();
        if (item.IsNew)
        {
            _definitionHeights.Remove(item);
            _viewModel.RemoveItem(item);
        }
    }

    private void WordTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _wordSelectionItem = textBox.DataContext as DictionaryItem;
            _wordSelectionStart = textBox.SelectionStart;
            _wordSelectionLength = textBox.SelectionLength;
            if (textBox.DataContext is DictionaryItem { IsAutoSaving: true })
                return;

            textBox.Select(0, 0);
            textBox.ScrollToHorizontalOffset(0);
        }
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
