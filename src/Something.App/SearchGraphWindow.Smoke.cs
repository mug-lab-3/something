using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;

namespace Something.App;

public sealed partial class SearchGraphWindow
{
    // One line with a point per scan, a size axis fitted around the sizes, and a marker for every
    // missing size.
    internal void VerifyForSmoke(int scans)
    {
        var line =
            Chart.Series.OfType<LineSeries<DateTimePoint>>().SingleOrDefault()
            ?? throw new InvalidOperationException("The graph has no line.");
        if (Chart.AnimationsSpeed != TimeSpan.Zero || Chart.EasingFunction is not null)
            throw new InvalidOperationException("The graph animates, so it trails a resize.");
        int points = line.Values?.Count ?? 0;
        if (points != scans)
            throw new InvalidOperationException(
                $"The graph has {points} points for {scans} scans."
            );
        double[] sizes =
        [
            .. line.Values!.Where(point => point.Value is not null)
                .Select(point => point.Value!.Value),
        ];
        if (
            Chart.YAxes.SingleOrDefault()
                is not { MinLimit: { } bottom, MaxLimit: { } top, CustomSeparators: { } ticks }
            || sizes.Length == 0
            || bottom > sizes.Min()
            || top < sizes.Max()
            || top <= bottom
            || ticks.Count() > 7
        )
            throw new InvalidOperationException("The size axis does not cover the sizes.");
        // Fitted to the sizes: the axis does not start at zero when they are far from it.
        if (bottom == 0 && sizes.Min() > (top - bottom) * 2)
            throw new InvalidOperationException("The size axis was not trimmed.");
        if (Chart.XAxes.SingleOrDefault() is not { LabelsRotation: not 0 })
            throw new InvalidOperationException("The time labels are not slanted.");
        int gaps = line.Values!.Count(point => point.Value is null);
        if (Chart.Sections.Count() != gaps)
            throw new InvalidOperationException("A missing size is not marked.");
    }

    internal Microsoft.UI.Xaml.FrameworkElement ContentForSmoke => Root;
}
