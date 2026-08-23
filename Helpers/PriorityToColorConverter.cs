using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Noted.Models;

namespace Noted.Helpers;

public sealed class PriorityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ChecklistPriority priority)
        {
            return priority switch
            {
                ChecklistPriority.High => new SolidColorBrush(Color.FromRgb(217, 104, 104)),
                ChecklistPriority.Medium => new SolidColorBrush(Color.FromRgb(214, 166, 75)),
                ChecklistPriority.Low => new SolidColorBrush(Color.FromRgb(91, 168, 200)),
                _ => new SolidColorBrush(Color.FromRgb(197, 210, 217))
            };
        }
        return new SolidColorBrush(Color.FromRgb(197, 210, 217));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
