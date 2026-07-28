using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;
using System.Windows.Media;

namespace Noted;

public partial class DictionaryWindow : OverlayWindow
{
    private readonly IAppSettingsService _settingsService;
    private readonly DictionaryWindowViewModel _viewModel;
    private readonly Dictionary<DictionaryItem, bool> _descriptionExpandedState = new();

    public DictionaryWindow(IAppSettingsService settingsService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _viewModel = new DictionaryWindowViewModel(settingsService, action => Dispatcher.Invoke(action));
        DataContext = _viewModel;

        // Load window state
        var windowState = _settingsService.LoadDictionaryWindowState();

        // Apply saved window position and size
        Left = windowState.Left;
        Top = windowState.Top;
        Width = windowState.Width;
        Height = windowState.Height;

        InitializeOverlay(windowState.GhostModeEnabled, windowState.GhostModeOpacity, windowState.Opacity);

        // Subscribe to ghost mode changes
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DictionaryWindowViewModel.GhostModeEnabled))
                ApplyGhostMode(_viewModel.GhostModeEnabled);
        };
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.AddItem();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                ItemsList.UpdateLayout();
                if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container)
                    return;

                var wordBox = FindChild<TextBox>(container, "WordTextBox");
                wordBox?.BringIntoView();
                wordBox?.Focus();
                wordBox?.SelectAll();
            }));
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DictionaryItem item)
        {
            _descriptionExpandedState.Remove(item);
            _viewModel.RemoveItem(item);
        }
    }

    private void DescriptionLink_Click(object sender, RoutedEventArgs e)
    {
        if (!(sender is Hyperlink link && link.Tag is DictionaryItem item))
            return;

        // Toggle stored expanded state
        var isExpanded = !_descriptionExpandedState.TryGetValue(item, out var expanded) ? true : !expanded;
        _descriptionExpandedState[item] = isExpanded;

        // Find the DataTemplate root border for this item
        var container = FindAncestor<Border>(link);
        if (container != null) {
            var descriptionBox = FindChild<TextBox>(container, "DescriptionBox");
            if (descriptionBox != null) {
                descriptionBox.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                if (isExpanded)
                    descriptionBox.Focus();
            }
        }

        e.Handled = true;
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
            GhostModeEnabled = _ghostModeEnabled
        };
        _settingsService.SaveDictionaryWindowState(newState);
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current != null) {
            if (current is T t)
                return t;
            // Prefer logical parent first (works well for Run/Hyperlink), then visual parent
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent, string? childName = null) where T : DependencyObject
    {
        if (parent == null)
            return null;
        int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++) {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T tChild) {
                if (string.IsNullOrEmpty(childName))
                    return tChild;
                if (child is FrameworkElement fe && fe.Name == childName)
                    return tChild;
            }

            var found = FindChild<T>(child, childName);
            if (found != null)
                return found;
        }
        return null;
    }
}
