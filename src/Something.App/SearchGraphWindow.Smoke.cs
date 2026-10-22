using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;

namespace Something.App;

public sealed partial class SearchGraphWindow
{
    // One line with a point per scan, a size axis from zero, and a marker for every missing size.
    internal void VerifyForSmoke(int scans)
    {
        var line =
            Chart.Series.OfType<LineSeries<DateTimePoint>>().SingleOrDefault()
            ?? throw new InvalidOperationException("The graph has no line.");
        int points = line.Values?.Count ?? 0;
        if (points != scans)
            throw new InvalidOperationException(
                $"The graph has {points} points for {scans} scans."
            );
        if (Chart.YAxes.SingleOrDefault() is not { MinLimit: 0, MaxLimit: > 0 })
            throw new InvalidOperationException("The size axis does not start at zero.");
        int gaps = line.Values!.Count(point => point.Value is null);
        if (Chart.Sections.Count() != gaps)
            throw new InvalidOperationException("A missing size is not marked.");
        SizeKindButtons.SelectedIndex = 1;
        SizeKindButtons.SelectedIndex = 0;
    }

    internal Microsoft.UI.Xaml.FrameworkElement ContentForSmoke => Root;
}
