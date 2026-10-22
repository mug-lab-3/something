using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Something.Application.History;

namespace Something.App;

/// <summary>A start and an end date and time, each edited in a flyout. Either may be empty.
/// Values are local times with a resolution of one minute.</summary>
public sealed partial class DateTimeRangeInput : UserControl
{
    private bool loading;

    public DateTimeRangeInput()
    {
        InitializeComponent();
        StartTime.Time = EndTime.Time = TimeSpan.Zero;
    }

    /// <summary>Raised after the user changed the start or the end; not for values set in code.</summary>
    public event EventHandler? RangeChanged;

    public DateTimeOffset? Start
    {
        get => Instant(StartDate, StartTime);
        set => Show(StartDate, StartTime, value);
    }

    public DateTimeOffset? End
    {
        get => Instant(EndDate, EndTime);
        set => Show(EndDate, EndTime, value);
    }

    private static DateTimeOffset? Instant(CalendarDatePicker date, TimePicker time) =>
        date.Date is { } value
            ? HistoryDeletion.LocalInstant(
                DateOnly.FromDateTime(value.DateTime),
                TimeOnly.FromTimeSpan(time.Time),
                TimeZoneInfo.Local
            )
            : null;

    private void Show(CalendarDatePicker date, TimePicker time, DateTimeOffset? value)
    {
        loading = true;
        try
        {
            var local = value?.ToLocalTime();
            date.Date = local;
            time.Time = local is { } shown ? new(shown.Hour, shown.Minute, 0) : TimeSpan.Zero;
        }
        finally
        {
            loading = false;
        }
        UpdateText();
    }

    private void UpdateText()
    {
        static string Display(CalendarDatePicker date, TimePicker time) =>
            date.Date is { } value ? value.Date.Add(time.Time).ToString("yyyy/MM/dd HH:mm") : "—";
        StartText.Text = Display(StartDate, StartTime);
        EndText.Text = Display(EndDate, EndTime);
    }

    private void Edited()
    {
        if (loading)
            return;
        UpdateText();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args
    ) => Edited();

    private void OnTimeChanged(object sender, TimePickerValueChangedEventArgs args) => Edited();

    private void OnClear(object sender, RoutedEventArgs args)
    {
        loading = true;
        StartDate.Date = EndDate.Date = null;
        StartTime.Time = EndTime.Time = TimeSpan.Zero;
        loading = false;
        Edited();
    }
}
