using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MyNotes.Helpers
{
    public class WidthToActionButtonVisibilityConverter : IValueConverter
    {
        // param: "delete" or "rename"
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width && parameter is string which)
            {
                if (which == "delete")
                {
                    // Hide delete button if width < 270
                    return width < 270 ? Visibility.Collapsed : Visibility.Visible;
                }
                if (which == "rename")
                {
                    // Hide rename button if width < 250
                    return width < 250 ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
