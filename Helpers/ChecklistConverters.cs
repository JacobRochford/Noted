using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Noted.Helpers;

public sealed class ChecklistScheduleTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = new List<string>();
        if (values[0] is DateTime date)
        {
            var due = values[1] is TimeSpan time ? date.Date.Add(time).ToString("g", culture) : date.ToString("d", culture);
            parts.Add($"Due {due}");
        }
        if (values[2] is DateTimeOffset reminder)
        {
            var when = values[3] is true ? "at due time" : reminder.ToLocalTime().ToString("g", culture);
            var style = values[4] is Noted.Models.ChecklistNotificationStyle.Urgent ? "Urgent reminder"
                : values.Length > 5 && values[5] is DateTimeOffset ? "Submitted" : "Reminder";
            parts.Add($"{style} {when}");
        }
        return parts.Count == 0 ? "Schedule..." : string.Join(" · ", parts);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DueDateToTextConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var label = Convert(values[0], targetType, parameter, culture);
        if (values[0] is not DateTime date || values[1] is not TimeSpan time) return label;
        if (date.Date == DateTime.Today && date.Date.Add(time) < DateTime.Now) label = "Overdue today";
        return $"{label} {new TimeOnly(time.Ticks).ToString("t", culture)}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

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

public sealed class DueDateToColorConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is DateTime date && values[1] is TimeSpan time && date.Date.Add(time) < DateTime.Now)
            return new SolidColorBrush(Color.FromRgb(190, 82, 82));
        return Convert(values[0], targetType, parameter, culture);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

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

// null → Collapsed, non-null → Visible
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ChecklistNotesVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string notes && !string.IsNullOrWhiteSpace(notes) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
