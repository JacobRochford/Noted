using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Noted.Helpers;

// Allows the tree view indentation width to be styled.
public sealed class IndentLevelToMarginConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value is int level && level > 0)
            return new Thickness(level * 14, 0, 0, 0);
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
