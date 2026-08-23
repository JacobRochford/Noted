using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Noted.Helpers;

public sealed class BoolToStrikethroughConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DueDateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime date) return "";
        var today = DateTime.Today;
        if (date.Date < today) return $"Overdue {date:MMM d}";
        if (date.Date == today) return "Today";
        if (date.Date == today.AddDays(1)) return "Tomorrow";
        return date.ToString("MMM d");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DueDateToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime date) return new SolidColorBrush(Colors.Transparent);
        var today = DateTime.Today;
        if (date.Date < today)  return new SolidColorBrush(Color.FromRgb(190, 82,  82));
        if (date.Date == today) return new SolidColorBrush(Color.FromRgb(190, 139, 60));
        return new SolidColorBrush(Color.FromRgb(91, 142, 168));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Binds a RadioButton's IsChecked to an enum value via ConverterParameter
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.Equals(parameter) ?? false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}

// null → Collapsed, non-null → Visible
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
