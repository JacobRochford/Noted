using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Noted.Models;

namespace Noted;

public partial class ChecklistPriorityColorsDialog : Window
{
    private static readonly IReadOnlyList<ChecklistColorChoice> ColorChoices =
        ChecklistColors.Choices;

    public ChecklistPriorityColors SelectedColors => new()
    {
        HighColor = GetSelectedColor(HighColorComboBox, ChecklistColors.DefaultHigh),
        MediumColor = GetSelectedColor(MediumColorComboBox, ChecklistColors.DefaultMedium),
        LowColor = GetSelectedColor(LowColorComboBox, ChecklistColors.DefaultLow)
    };

    public ChecklistPriorityColorsDialog(ChecklistPriorityColors colors)
    {
        InitializeComponent();
        HighColorComboBox.ItemsSource = ColorChoices;
        MediumColorComboBox.ItemsSource = ColorChoices;
        LowColorComboBox.ItemsSource = ColorChoices;
        SetSelections(ChecklistColors.Normalize(colors));
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelections(new ChecklistPriorityColors());
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void SetSelections(ChecklistPriorityColors colors)
    {
        SelectColor(HighColorComboBox, colors.HighColor);
        SelectColor(MediumColorComboBox, colors.MediumColor);
        SelectColor(LowColorComboBox, colors.LowColor);
    }

    private static void SelectColor(ComboBox comboBox, string hex)
    {
        comboBox.SelectedItem = ColorChoices.First(choice =>
            string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSelectedColor(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ChecklistColorChoice choice
            ? choice.Hex
            : fallback;
    }
}
