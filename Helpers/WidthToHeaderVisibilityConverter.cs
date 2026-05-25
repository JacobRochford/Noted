using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Noted.Helpers;

public class WidthToHeaderVisibilityConverter : IValueConverter
{
    private const double HeaderHideWidth = 340;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width)
            return width < HeaderHideWidth ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class WidthBelowThresholdConverter : IValueConverter
{
    public double Threshold { get; set; } = 320;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d && d <= Threshold;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
