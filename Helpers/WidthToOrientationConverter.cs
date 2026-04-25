using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace MyNotes.Helpers
{
    public class WidthToOrientationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width)
            {
                // Use vertical if width is less than 340, else horizontal
                return width < 340 ? Orientation.Vertical : Orientation.Horizontal;
            }
            return Orientation.Horizontal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
