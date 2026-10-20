using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Something.App;

internal sealed class HistoryPickerRow(HistoryRow history, bool current) : INotifyPropertyChanged
{
    public HistoryRow History { get; } = history;
    public string TimeText =>
        History.Entry.CompletedLocal.ToString("yyyy/MM/dd HH:mm:ss")
        + (
            current ? " · 選択中"
            : History.IsLatest ? " · 最新"
            : ""
        );
    public string CountText { get; private set; } = "—";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetCount(long count)
    {
        CountText = count.ToString("N0");
        PropertyChanged?.Invoke(this, new(nameof(CountText)));
    }
}

internal sealed class HistoryPickerGroup(string label, IEnumerable<HistoryPickerRow> rows)
    : List<HistoryPickerRow>(rows)
{
    public string Label { get; } = label;
}

public sealed partial class HistorySelectionDialog : ContentDialog
{
    private readonly HistoryPickerRow[] rows;
    private readonly Dictionary<Guid, long> counts;
    private readonly Func<Guid[], Task<IReadOnlyDictionary<Guid, long>>>? loadCounts;
    private readonly Dictionary<Guid, HistoryPickerRow> pending = [];
    private readonly HashSet<DateOnly> recordedDays;
    private readonly DateOnly today = DateOnly.FromDateTime(DateTime.Today);
    private Guid? candidate;
    private CalendarView? calendar;
    private bool updatingCalendar;
    private bool loadingCounts;
    private bool closed;
    private Task countsTask = Task.CompletedTask;
    private HistoryPickerRow[] visible = [];
    internal string? OtherEndpoint { get; init; }
    internal bool Invalidated { get; private set; }
    internal HistoryRow? SelectedHistory => (Records.SelectedItem as HistoryPickerRow)?.History;

    internal HistorySelectionDialog(
        HistoryRow[] histories,
        HistoryRow? selected,
        Dictionary<Guid, long> counts,
        Func<Guid[], Task<IReadOnlyDictionary<Guid, long>>>? loadCounts
    )
    {
        InitializeComponent();
        rows = histories
            .Select(row => new HistoryPickerRow(row, row.Entry.Id == selected?.Entry.Id))
            .ToArray();
        this.counts = counts;
        this.loadCounts = loadCounts;
        candidate = selected?.Entry.Id;
        recordedDays = rows.Select(DayOf).ToHashSet();
    }

    private static DateOnly DayOf(HistoryPickerRow row) =>
        DateOnly.FromDateTime(row.History.Entry.CompletedLocal.DateTime);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Keep the SDK's dialog buttons and keyboard behavior; remove its equal-width stretching.
        if (GetTemplateChild("CommandSpace") is Grid commands)
        {
            commands.HorizontalAlignment = HorizontalAlignment.Right;
            foreach (var column in commands.ColumnDefinitions)
                if (column.Width.IsStar)
                    column.Width = GridLength.Auto;
        }
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        DialogContent.Width = Math.Min(860, Math.Max(320, XamlRoot.Size.Width - 96));
        DialogContent.Height = Math.Min(500, Math.Max(240, XamlRoot.Size.Height - 220));
        ContextText.Text =
            OtherEndpoint ?? "一覧からスキャンを選び、「選択」で表示を切り替えます。";
        calendar = new CalendarView
        {
            SelectionMode = CalendarViewSelectionMode.Single,
            IsTodayHighlighted = true,
        };
        calendar.CalendarViewDayItemChanging += OnDayItemChanging;
        calendar.SelectedDatesChanged += OnDayChanged;
        if (rows.Length > 0)
        {
            var first = new DateTime(recordedDays.Min().Year, 1, 1);
            var last = new DateTime(recordedDays.Max().Year, 12, 31);
            if (first > calendar.MaxDate.DateTime)
                calendar.MaxDate = new DateTimeOffset(last);
            calendar.MinDate = new DateTimeOffset(first);
            calendar.MaxDate = new DateTimeOffset(last);
            calendar.SetDisplayDate(
                (rows.FirstOrDefault(row => row.History.Entry.Id == candidate) ?? rows[0])
                    .History
                    .Entry
                    .CompletedLocal
            );
        }
        CalendarHost.Child = calendar;
        ApplyPeriod("All");
    }

    internal void Invalidate()
    {
        Invalidated = true;
        Hide();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        closed = true;
        pending.Clear();
    }

    private void OnDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        bool hasRecords = recordedDays.Contains(DateOnly.FromDateTime(args.Item.Date.DateTime));
        var brush = (SolidColorBrush)
            Microsoft.UI.Xaml.Application.Current.Resources["SystemControlHighlightAccentBrush"];
        args.Item.SetDensityColors(hasRecords ? [brush.Color] : []);
        ToolTipService.SetToolTip(args.Item, hasRecords ? "スキャンがあります" : "スキャンなし");
    }

    private void OnPeriod(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: string tag })
            ApplyPeriod(tag);
    }

    private void ApplyPeriod(string period)
    {
        updatingCalendar = true;
        calendar?.SelectedDates.Clear();
        updatingCalendar = false;
        SetPeriodButtons(period);
        DateOnly? start = period switch
        {
            "Week" => today.AddDays(-6),
            "Month" => new(today.Year, today.Month, 1),
            "Year" => new(today.Year, 1, 1),
            _ => null,
        };
        ShowRows(
            start is { } from ? rows.Where(row => DayOf(row) >= from && DayOf(row) <= today) : rows,
            period switch
            {
                "Week" => "直近 7 日",
                "Month" => "今月",
                "Year" => "今年",
                _ => "すべてのスキャン",
            }
        );
    }

    private void SetPeriodButtons(string period)
    {
        foreach (var button in new[] { AllButton, WeekButton, MonthButton, YearButton })
            button.IsChecked = (string)button.Tag == period;
    }

    private void OnDayChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (updatingCalendar || sender.SelectedDates.Count == 0)
            return;
        ShowDate(DateOnly.FromDateTime(sender.SelectedDates[0].DateTime));
    }

    private void ShowDate(DateOnly day)
    {
        SetPeriodButtons("");
        ShowRows(rows.Where(row => DayOf(row) == day), day.ToString("yyyy/MM/dd"));
    }

    private void ShowRows(IEnumerable<HistoryPickerRow> matches, string label)
    {
        visible = matches.ToArray();
        var groups = visible
            .GroupBy(row =>
                DayOf(row) == today ? "今日"
                : DayOf(row) == today.AddDays(-1) ? "昨日"
                : DayOf(row).ToString("yyyy年M月")
            )
            .Select(group => new HistoryPickerGroup(group.Key, group))
            .ToArray();
        var source = new CollectionViewSource { IsSourceGrouped = true, Source = groups };
        Records.ItemsSource = source.View;
        Records.SelectedItem = visible.FirstOrDefault(row => row.History.Entry.Id == candidate);
        // Opening and clearing a filter always show the newest records first.
        if (visible.Length > 0)
            Records.ScrollIntoView(visible[0]);
        RangeText.Text = $"{label} · {visible.Length:N0} 件";
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = Records.SelectedItem is not null;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Records.SelectedItem is HistoryPickerRow row)
            candidate = row.History.Entry.Id;
        IsPrimaryButtonEnabled = Records.SelectedItem is not null;
    }

    private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not HistoryPickerRow row || closed)
            return;
        if (counts.TryGetValue(row.History.Entry.Id, out long count))
        {
            row.SetCount(count);
            return;
        }
        pending[row.History.Entry.Id] = row;
        if (!loadingCounts && loadCounts is not null)
        {
            loadingCounts = true;
            countsTask = LoadPendingAsync();
        }
    }

    private async Task LoadPendingAsync()
    {
        // Coalesce the visible virtualized rows before issuing a bounded count query.
        await Task.Yield();
        try
        {
            while (pending.Count > 0 && !closed)
            {
                var batch = pending.Values.Take(64).ToArray();
                foreach (var row in batch)
                    pending.Remove(row.History.Entry.Id);
                var loaded = await loadCounts!(batch.Select(row => row.History.Entry.Id).ToArray());
                if (closed || Invalidated)
                    return;
                foreach (var row in batch)
                    if (loaded.TryGetValue(row.History.Entry.Id, out long count))
                    {
                        counts[row.History.Entry.Id] = count;
                        row.SetCount(count);
                    }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!closed)
                CountError.Visibility = Visibility.Visible;
            pending.Clear();
        }
        finally
        {
            loadingCounts = false;
        }
    }

    internal async Task VerifyAsync(CancellationToken token)
    {
        if (visible.Length != rows.Length || AllButton.IsChecked != true)
            throw new InvalidOperationException(
                "History selection did not start with every record."
            );
        var original = candidate;
        foreach (string period in new[] { "Week", "Month", "Year" })
        {
            ApplyPeriod(period);
            if (candidate != original)
                throw new InvalidOperationException("Filtering committed a different history.");
        }
        ShowDate(recordedDays.Min().AddDays(-1));
        if (visible.Length != 0 || IsPrimaryButtonEnabled)
            throw new InvalidOperationException("Empty date retained a selectable hidden history.");
        ApplyPeriod("All");
        if (visible.Length != rows.Length || candidate != original)
            throw new InvalidOperationException("Clearing the filter did not restore all records.");
        if (visible.Length > 1)
        {
            Records.SelectedItem = visible.First(row => row.History.Entry.Id != original);
            if (SelectedHistory?.Entry.Id == original || !IsPrimaryButtonEnabled)
                throw new InvalidOperationException("A candidate record could not be selected.");
            Records.SelectedItem = visible.FirstOrDefault(row => row.History.Entry.Id == original);
        }
        Records.UpdateLayout();
        await Task.Delay(200, token);
        await countsTask.WaitAsync(token);
        if (visible.Take(Math.Min(2, visible.Length)).Any(row => row.CountText == "—"))
            throw new InvalidOperationException("Visible history counts did not load.");
        if (
            GetTemplateChild("PrimaryButton") is not Button primary
            || GetTemplateChild("CloseButton") is not Button cancel
            || primary.ActualWidth is < 96 or > 160
            || cancel.ActualWidth is < 96 or > 160
            || primary.ActualHeight > 36
            || cancel.ActualHeight > 36
            || primary.CornerRadius.TopLeft < 4
            || cancel.CornerRadius.TopLeft < 4
            || GetTemplateChild("CommandSpace")
                is not Grid { HorizontalAlignment: HorizontalAlignment.Right }
        )
            throw new InvalidOperationException(
                "History dialog buttons are not compact and rounded."
            );
    }
}
