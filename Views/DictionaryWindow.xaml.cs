using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Helpers;
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
        var windowState = _settingsService.LoadDictionaryWindowState();
        RestoreWindowBounds(
            windowState.Left,
            windowState.Top,
            windowState.Width,
            windowState.Height);

        _viewModel = new DictionaryWindowViewModel(
            settingsService,
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

                var wordBox = VisualTreeHelpers.FindDescendant<TextBox>(container, "WordTextBox");
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
        var container = VisualTreeHelpers.FindAncestor<Border>(link);
        if (container != null) {
            var descriptionBox = VisualTreeHelpers.FindDescendant<TextBox>(container, "DescriptionBox");
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

}
