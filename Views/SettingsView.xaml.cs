using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Noted.Services;
using Noted.ViewModels;

namespace Noted;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public SettingsView()
    {
        InitializeComponent();
        SettingsCategory.SelectionChanged += (_, _) => UpdateCategory();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshFileExplorerIntegration();
        };
        UpdateCategory();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is SettingsViewModel previous)
                previous.PropertyChanged -= OnViewModelPropertyChanged;
            if (args.NewValue is SettingsViewModel current)
                current.PropertyChanged += OnViewModelPropertyChanged;
            RefreshStatusResources();
        };
    }

    private void UpdateCategory()
    {
        var category = (SettingsCategory.SelectedItem as ComboBoxItem)?.Tag;
        foreach (FrameworkElement section in SettingsSections.Children)
            section.Visibility = Equals(section.Tag, category) ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel is { } model) model.IsHotkeyEditorOpen = false;
        SettingsScrollViewer.ScrollToTop();
        if (Equals(category, "Integration")) RefreshFileExplorerIntegration();
    }

    private void RefreshFileExplorerIntegration() =>
        FileExplorerIntegrationOption.IsChecked = FileExplorerIntegrationService.IsEnabled();

    private void FileExplorerIntegrationOption_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileExplorerIntegrationService.SetEnabled(FileExplorerIntegrationOption.IsChecked == true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            ExceptionDiagnostics.Record(ex);
            AppDialog.Show("The File Explorer integration could not be updated. Check registry access.",
                "Unable to update File Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshFileExplorerIntegration();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is
            nameof(SettingsViewModel.AccentHasError) or nameof(SettingsViewModel.BackupStatusResource))
            RefreshStatusResources();
    }

    private void RefreshStatusResources()
    {
        if (ViewModel is not { } model) return;
        AccentValidationText.SetResourceReference(TextBlock.ForegroundProperty,
            model.AccentHasError ? "NotedDangerBrush" : "NotedMutedTextBrush");
        AccentHexTextBox.SetResourceReference(Control.BorderBrushProperty,
            model.AccentHasError ? "NotedDangerBrush" : "NotedBorderBrush");
        BackupStatusText.SetResourceReference(TextBlock.ForegroundProperty, model.BackupStatusResource);
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color } && ViewModel is { } model)
            model.AccentText = color;
    }

    private void ChooseAccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;
        var dialog = new AccentColorDialog(model.ActiveAccent) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) model.AccentText = dialog.SelectedColor;
    }

    private void AccentHexTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ViewModel?.NormalizeAccentText();

    private void TimestampLineTextBox_LostFocus(object sender, RoutedEventArgs e) =>
        ViewModel?.CommitTimestampLine();

    private void EditHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string featureText } button
            || !Enum.TryParse(featureText, out HotkeyFeature feature) || ViewModel is not { } model)
            return;
        HotkeyEditorPopup.PlacementTarget = button;
        model.BeginHotkeyEdit(feature);
        Dispatcher.BeginInvoke(() => HotkeyKeyCombo.Focus(), DispatcherPriority.Input);
    }
}
