using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Noted;

public partial class ChecklistTimePicker : UserControl
{
    public static readonly DependencyProperty SelectedTimeProperty = DependencyProperty.Register(
        nameof(SelectedTime), typeof(TimeSpan?), typeof(ChecklistTimePicker),
        new FrameworkPropertyMetadata(null, OnTimeChanged),
        value => value is null || value is TimeSpan time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1));

    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(ChecklistTimePicker),
        new PropertyMetadata(true, (sender, _) => ((ChecklistTimePicker)sender).RefreshClock()));

    public TimeSpan? SelectedTime
    {
        get => (TimeSpan?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    public event RoutedEventHandler? SelectedTimeChanged;
    private bool _updating;
    private readonly bool _use24Hour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');

    public ChecklistTimePicker()
    {
        InitializeComponent();
        _updating = true;
        HourChoice.ItemsSource = Enumerable.Range(_use24Hour ? 0 : 1, _use24Hour ? 24 : 12).Select(hour => hour.ToString(CultureInfo.CurrentCulture));
        MinuteChoice.ItemsSource = Enumerable.Range(0, 60).Select(minute => minute.ToString("00", CultureInfo.InvariantCulture));
        PeriodChoice.ItemsSource = new[] { CultureInfo.CurrentCulture.DateTimeFormat.AMDesignator, CultureInfo.CurrentCulture.DateTimeFormat.PMDesignator };
        PeriodChoice.Visibility = _use24Hour ? Visibility.Collapsed : Visibility.Visible;
        HourChoice.SelectedIndex = _use24Hour ? 9 : 8;
        MinuteChoice.SelectedIndex = PeriodChoice.SelectedIndex = 0;
        _updating = false;
        RefreshClock();
    }

    private static void OnTimeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ChecklistTimePicker)sender;
        picker.RefreshClock();
        picker.SelectedTimeChanged?.Invoke(picker, new RoutedEventArgs());
    }

    private void RefreshClock()
    {
        if (HourChoice is null) return;
        _updating = true;
        UseTime.Visibility = AllowEmpty ? Visibility.Visible : Visibility.Collapsed;
        UseTime.IsChecked = SelectedTime.HasValue;
        UseTime.Content = SelectedTime.HasValue ? "Time" : "Add time";
        ClockFields.Visibility = !AllowEmpty || SelectedTime.HasValue ? Visibility.Visible : Visibility.Collapsed;
        HourChoice.IsEnabled = MinuteChoice.IsEnabled = PeriodChoice.IsEnabled = !AllowEmpty || SelectedTime.HasValue;
        if (SelectedTime is { } time)
        {
            HourChoice.SelectedIndex = _use24Hour ? time.Hours : (time.Hours + 11) % 12;
            MinuteChoice.SelectedIndex = time.Minutes;
            PeriodChoice.SelectedIndex = time.Hours >= 12 ? 1 : 0;
        }
        _updating = false;
    }

    internal void Suggest(TimeSpan time)
    {
        if (SelectedTime.HasValue) return;
        _updating = true;
        HourChoice.SelectedIndex = _use24Hour ? time.Hours : (time.Hours + 11) % 12;
        MinuteChoice.SelectedIndex = time.Minutes;
        PeriodChoice.SelectedIndex = time.Hours >= 12 ? 1 : 0;
        _updating = false;
    }

    private void UseTime_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || HourChoice is null) return;
        if (UseTime.IsChecked == true) UpdateTime();
        else SetCurrentValue(SelectedTimeProperty, null);
    }

    private void Clock_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) UpdateTime();
    }

    private void UpdateTime()
    {
        if (HourChoice.SelectedIndex < 0 || MinuteChoice.SelectedIndex < 0 || PeriodChoice.SelectedIndex < 0) return;
        var hour = _use24Hour ? HourChoice.SelectedIndex : (HourChoice.SelectedIndex + 1) % 12 + PeriodChoice.SelectedIndex * 12;
        SetCurrentValue(SelectedTimeProperty, new TimeSpan(hour, MinuteChoice.SelectedIndex, 0));
    }
}
