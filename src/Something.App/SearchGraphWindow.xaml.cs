using System.Collections.Immutable;
using System.Globalization;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Microsoft.UI.Xaml;
using SkiaSharp;
using Something.Application;
using Something.Application.Search;
using Windows.Graphics;

namespace Something.App;

/// <summary>The size of one search item in every scan of the search, over time. A scan in which
/// the item was not seen, or whose size is unresolved, breaks the line and is marked; it is never
/// drawn as zero. One window per item.</summary>
public sealed partial class SearchGraphWindow : Window
{
    private readonly HistorySearchResult result;
    private readonly ImmutableArray<SearchSizeObservation> series;
    private bool ready;

    public SearchGraphWindow(
        SearchRow row,
        HistorySearchResult result,
        ImmutableArray<SearchSizeObservation> series,
        ElementTheme theme
    )
    {
        this.result = result;
        this.series = series;
        InitializeComponent();
        Title = Strings.Format("Graph_WindowTitle", row.Name);
        PathText.Text = row.Path;
        AppWindow.Resize(new SizeInt32(960, 600));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Something.ico"));
        Root.RequestedTheme = theme;
        // The chart eases its marks into place by default, which makes it trail behind the
        // window while it is resized.
        Chart.AnimationsSpeed = TimeSpan.Zero;
        Chart.EasingFunction = null;
        ready = true;
        Root.ActualThemeChanged += (_, _) => Draw();
        Closed += (_, _) =>
        {
            ready = false;
            Chart.Series = [];
        };
        Draw();
    }

    private void Draw()
    {
        if (!ready)
            return;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var text = new SolidColorPaint(dark ? new SKColor(235, 235, 235) : new SKColor(30, 30, 30));
        var grid = new SolidColorPaint(
            dark ? new SKColor(255, 255, 255, 40) : new SKColor(0, 0, 0, 40),
            1
        );
        var accent = dark ? new SKColor(96, 175, 240) : new SKColor(0, 90, 158);
        var kind = result.Criteria.SizeKind;
        var points = SearchSizeSeries.Build(result, series, kind);
        var known = points
            .Where(point => point.Bytes is not null)
            .Select(point => point.Bytes!.Value);
        // The axis is fitted to the sizes, with room around them.
        var scale = known.Any()
            ? SearchSizeSeries.ScaleFor(known.Min(), known.Max())
            : SearchSizeSeries.ScaleFor(0, 0);
        string unit = UnitName(scale.Unit);
        // The window shows the path of the item; a scan in which it had another one says so.
        string? shownPath = points.LastOrDefault(point => point.Path is not null)?.Path;

        var values = points
            .Select(point => new DateTimePoint(
                point.CompletedUtc.LocalDateTime,
                point.Bytes is { } bytes ? (double)bytes : null
            ))
            .ToArray();
        var line = new LineSeries<DateTimePoint>(values)
        {
            Name = "", // The tooltip shows the size, not a legend entry.
            LineSmoothness = 0,
            GeometrySize = 8,
            EnableNullSplitting = true,
            Fill = null,
            Stroke = new SolidColorPaint(accent, 2),
            GeometryStroke = new SolidColorPaint(accent, 2),
            GeometryFill = new SolidColorPaint(dark ? new SKColor(32, 32, 32) : SKColors.White),
            XToolTipLabelFormatter = point =>
                points[point.Index]
                    .CompletedUtc.ToLocalTime()
                    .ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture),
            YToolTipLabelFormatter = point =>
                points[point.Index].Path is { } path
                && !string.Equals(path, shownPath, StringComparison.Ordinal)
                    ? Strings.Format(
                        "Graph_Tooltip",
                        DisplayText.Bytes(points[point.Index].Bytes),
                        DisplayText.Escape(path)
                    )
                    : DisplayText.Bytes(points[point.Index].Bytes),
        };
        Chart.Series = [line];
        Chart.Sections =
        [
            .. points
                .Where(point => point.Bytes is null)
                .Select(point => new RectangularSection
                {
                    Xi = point.CompletedUtc.LocalDateTime.Ticks,
                    Xj = point.CompletedUtc.LocalDateTime.Ticks,
                    Stroke = new SolidColorPaint(accent.WithAlpha(150), 1.5f)
                    {
                        PathEffect = new DashEffect([5, 5]),
                    },
                }),
        ];
        // The unit and label of the time axis follow the span the scans cover.
        var span =
            points.Length < 2 ? TimeSpan.Zero : points[^1].CompletedUtc - points[0].CompletedUtc;
        (TimeSpan unitOfTime, string format) = span switch
        {
            _ when span < TimeSpan.FromMinutes(3) => (TimeSpan.FromSeconds(1), "HH:mm:ss"),
            _ when span < TimeSpan.FromHours(3) => (TimeSpan.FromMinutes(1), "HH:mm"),
            _ when span < TimeSpan.FromDays(2) => (TimeSpan.FromHours(1), "MM/dd HH:mm"),
            _ => (TimeSpan.FromDays(1), "yyyy/MM/dd"),
        };
        Chart.XAxes =
        [
            new Axis
            {
                UnitWidth = unitOfTime.Ticks,
                MinStep = unitOfTime.Ticks,
                Labeler = value => SafeTime(value).ToString(format, CultureInfo.CurrentCulture),
                // Slanted, so that wide labels do not run into the next one.
                LabelsRotation = -45,
                LabelsPaint = text,
                SeparatorsPaint = grid,
            },
        ];
        Chart.YAxes =
        [
            new Axis
            {
                Name =
                    kind == SearchSizeKind.Logical
                        ? Strings.Format("Graph_SizeAxisLogical", unit)
                        : Strings.Format("Graph_SizeAxis", unit),
                NamePaint = text,
                NameTextSize = 13,
                MinLimit = (double)scale.Bottom,
                MaxLimit = (double)scale.Top,
                CustomSeparators = [.. scale.Ticks().Select(tick => (double)tick)],
                Labeler = value =>
                    ((double)value / (double)scale.Unit).ToString(
                        $"F{scale.Decimals}",
                        CultureInfo.CurrentCulture
                    ),
                LabelsPaint = text,
                SeparatorsPaint = grid,
            },
        ];
        Chart.LegendPosition = LegendPosition.Hidden;
    }

    private static DateTime SafeTime(double value) =>
        value is >= 0 and < 3_155_378_975_999_999_999
            ? new DateTime((long)value)
            : DateTime.MinValue;

    private static string UnitName(UInt128 unit)
    {
        string[] names = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];
        int index = 0;
        while (unit > 1 && index < names.Length - 1)
        {
            unit >>= 10;
            index++;
        }
        return names[index];
    }
}
