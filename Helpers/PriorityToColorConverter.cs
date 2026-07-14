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
                ChecklistPriority.High => new SolidColorBrush(Color.FromRgb(239, 68, 68)),    // Red
                ChecklistPriority.Medium => new SolidColorBrush(Color.FromRgb(249, 115, 22)), // Orange
                ChecklistPriority.Low => new SolidColorBrush(Color.FromRgb(234, 179, 8)),     // Yellow
                _ => new SolidColorBrush(Color.FromRgb(229, 231, 235))                        // Light Gray
            };
        }
        return new SolidColorBrush(Color.FromRgb(229, 231, 235));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
