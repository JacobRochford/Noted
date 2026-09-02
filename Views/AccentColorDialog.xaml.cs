using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Noted.Models;

namespace Noted;

public partial class AccentColorDialog : Window
{
    private bool _updatingControls;

    public string SelectedColor { get; private set; }

    public AccentColorDialog(string currentColor)
    {
        _updatingControls = true;
        InitializeComponent();
        SelectedColor = AppTheme.NormalizeAccentColor(currentColor);
        SetControlsFromColor(SelectedColor);
        _updatingControls = false;
        UpdatePreview(SelectedColor);
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls || !IsInitialized)
            return;

        var color = Color.FromRgb(
            (byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value),
            (byte)Math.Round(BlueSlider.Value));
        SetSelectedColor($"#{color.R:X2}{color.G:X2}{color.B:X2}", updateSliders: false);
    }

    private void HexTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updatingControls)
            return;

        if (!AppTheme.TryNormalizeAccentColor(HexTextBox.Text, out var color))
        {
            ValidationText.Text = "Enter six hexadecimal digits.";
            return;
        }

        ValidationText.Text = string.Empty;
        SetSelectedColor(color, updateSliders: true);
    }

    private void UseColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppTheme.TryNormalizeAccentColor(HexTextBox.Text, out var color))
        {
            ValidationText.Text = "Enter six hexadecimal digits.";
            HexTextBox.Focus();
            return;
        }

        SelectedColor = color;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void SetSelectedColor(string color, bool updateSliders)
    {
        SelectedColor = color;
        if (updateSliders)
            SetControlsFromColor(color);
        else
            SetHexText(color);

        UpdatePreview(color);
    }

    private void SetControlsFromColor(string color)
    {
        var parsed = ParseColor(color);
        _updatingControls = true;
        try
        {
            RedSlider.Value = parsed.R;
            GreenSlider.Value = parsed.G;
            BlueSlider.Value = parsed.B;
            RedValue.Text = parsed.R.ToString(CultureInfo.InvariantCulture);
            GreenValue.Text = parsed.G.ToString(CultureInfo.InvariantCulture);
            BlueValue.Text = parsed.B.ToString(CultureInfo.InvariantCulture);
            HexTextBox.Text = color;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void SetHexText(string color)
    {
        _updatingControls = true;
        try
        {
            RedValue.Text = Math.Round(RedSlider.Value).ToString(CultureInfo.InvariantCulture);
            GreenValue.Text = Math.Round(GreenSlider.Value).ToString(CultureInfo.InvariantCulture);
            BlueValue.Text = Math.Round(BlueSlider.Value).ToString(CultureInfo.InvariantCulture);
            HexTextBox.Text = color;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void UpdatePreview(string color)
    {
        var brush = new SolidColorBrush(ParseColor(color));
        brush.Freeze();
        AccentPreview.Background = brush;
    }

    private static Color ParseColor(string color) => Color.FromRgb(
        byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
