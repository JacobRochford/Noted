using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Noted.Helpers;

public class WidthToActionButtonVisibilityConverter : IValueConverter
{
    private const double DeleteButtonHideWidth = 270;
    private const double RenameButtonHideWidth = 250;

    // Converter parameter: "delete" or "rename".
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && parameter is string which)
        {
            if (which == "delete")
                return width < DeleteButtonHideWidth ? Visibility.Collapsed : Visibility.Visible;
            if (which == "rename")
                return width < RenameButtonHideWidth ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
