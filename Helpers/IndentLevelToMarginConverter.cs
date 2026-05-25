using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Noted.Helpers;

// Treeview indent workaround, WPF doesn't let you set it
// Used for the tree view node's indent depth level, since WPF's built-in TreeView doesn't allow customizing the indentation amount
public sealed class IndentLevelToMarginConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value is int level && level > 0)
            return new Thickness(level * 14, 0, 0, 0);
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
