using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Noted;

public sealed record AppDialogResult(
    MessageBoxResult Result,
    bool DoNotShowAgain);

public partial class AppDialog : Window
{
    private MessageBoxResult _result;

    private AppDialog(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfigureIcon(image);
        ConfigureButtons(buttons);
    }

    public static MessageBoxResult Show(
        string message)
    {
        return ShowCore(owner: null, message, "Noted", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static MessageBoxResult Show(
        string message,
        string title)
    {
        return ShowCore(owner: null, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons)
    {
        return ShowCore(owner: null, message, title, buttons, MessageBoxImage.Information);
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        return ShowCore(owner: null, message, title, buttons, image);
    }

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string title,
        MessageBoxButton buttons)
    {
        return ShowCore(owner, message, title, buttons, MessageBoxImage.Information);
    }

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        return ShowCore(owner, message, title, buttons, image);
    }

    public static AppDialogResult ShowWithSuppression(
        Window owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        string suppressionText)
    {
        var dialog = new AppDialog(message, title, buttons, image);
        dialog.SuppressionCheckBox.Content = suppressionText;
        dialog.SuppressionCheckBox.Visibility = Visibility.Visible;
        ShowDialogCore(dialog, owner);
        return new AppDialogResult(
            dialog._result,
            dialog.SuppressionCheckBox.IsChecked == true);
    }

    private static MessageBoxResult ShowCore(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var dialog = new AppDialog(message, title, buttons, image);
        ShowDialogCore(dialog, owner);
        return dialog._result;
    }

    private static void ShowDialogCore(AppDialog dialog, Window? owner)
    {
        var resolvedOwner = owner?.IsVisible == true
            ? owner
            : Application.Current?.MainWindow is { IsVisible: true } mainWindow
                ? mainWindow
                : null;
        if (resolvedOwner is not null)
            dialog.Owner = resolvedOwner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        (IconText.Text, IconText.Foreground) = image switch
        {
            MessageBoxImage.Error => ("×", new SolidColorBrush(Color.FromRgb(183, 65, 65))),
            MessageBoxImage.Warning => ("!", new SolidColorBrush(Color.FromRgb(184, 118, 31))),
            MessageBoxImage.Question => ("?", new SolidColorBrush(Color.FromRgb(44, 110, 145))),
            _ => ("i", new SolidColorBrush(Color.FromRgb(44, 110, 145)))
        };
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                _result = MessageBoxResult.Cancel;
                AddButton("Cancel", MessageBoxResult.Cancel, isCancel: true);
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isPrimary: true);
                break;
            case MessageBoxButton.YesNo:
                _result = MessageBoxResult.No;
                AddButton("No", MessageBoxResult.No, isCancel: true);
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isPrimary: true);
                break;
            case MessageBoxButton.YesNoCancel:
                _result = MessageBoxResult.Cancel;
                AddButton("Cancel", MessageBoxResult.Cancel, isCancel: true);
                AddButton("No", MessageBoxResult.No);
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isPrimary: true);
                break;
            default:
                _result = MessageBoxResult.OK;
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: true, isPrimary: true);
                break;
        }
    }

    private void AddButton(
        string label,
        MessageBoxResult result,
        bool isDefault = false,
        bool isCancel = false,
        bool isPrimary = false)
    {
        var button = new Button
        {
            Content = label,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Style = (Style)FindResource(isPrimary ? "PrimaryDialogButton" : "DialogButton")
        };
        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = true;
        };
        ButtonsPanel.Children.Add(button);
    }
}
