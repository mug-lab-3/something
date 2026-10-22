using System.Collections.Immutable;
using System.Numerics;
using Something.Application.Search;

namespace Something.Application;

/// <summary>One scan of a size graph. A scan without a known size has no <see cref="Bytes"/>: it
/// is drawn as a break in the line, never as zero.</summary>
/// <param name="CompletedUtc">When the scan completed; the position on the time axis.</param>
/// <param name="Bytes">The size in the scan, or null when the item was not observed or its size is unresolved.</param>
/// <param name="State">Why the size is known or missing.</param>
/// <param name="Path">The path of the item in that scan, or null when it was not observed.</param>
public sealed record SearchSizePoint(
    DateTimeOffset CompletedUtc,
    UInt128? Bytes,
    CapacityObservationState State,
    string? Path
);

/// <summary>The visible range of a size axis and where its ticks fall: the multiples of
/// <see cref="Step"/> within <see cref="Bottom"/> to <see cref="Top"/>, labeled in multiples of
/// <see cref="Unit"/> with <see cref="Decimals"/> decimals.</summary>
public readonly record struct SizeAxisScale(
    UInt128 Unit,
    UInt128 Bottom,
    UInt128 Step,
    UInt128 Top,
    int Decimals = 0
)
{
    /// <summary>The tick values, from the first multiple of the step in the range.</summary>
    public IEnumerable<UInt128> Ticks()
    {
        UInt128 remainder = Bottom % Step;
        UInt128 gap = remainder == 0 ? 0 : Step - remainder;
        if (gap > Top - Bottom)
            yield break;
        for (UInt128 tick = Bottom + gap; ; tick += Step)
        {
            yield return tick;
            if (Top - tick < Step)
                yield break;
        }
    }
}

/// <summary>Turns the sizes of one search item into the points and axes of its graph.</summary>
public static class SearchSizeSeries
{
    /// <summary>One point per scan of the result, oldest first. The observations must come from
    /// a series read for <paramref name="result"/>: one per scan, in the order of the scans.</summary>
    public static ImmutableArray<SearchSizePoint> Build(
        HistorySearchResult result,
        ImmutableArray<SearchSizeObservation> observations,
        SearchSizeKind kind
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (observations.IsDefault || observations.Length != result.Scans.Length)
            throw new ArgumentException(
                "The series must have one observation per scan of the result.",
                nameof(observations)
            );
        var points = ImmutableArray.CreateBuilder<SearchSizePoint>(observations.Length);
        for (int index = 0; index < observations.Length; index++)
        {
            var observation = observations[index];
            if (observation.ScanIndex != index)
                throw new ArgumentException(
                    "The observations are not in the order of the scans.",
                    nameof(observations)
                );
            UInt128? bytes =
                observation.State == CapacityObservationState.Known
                    ? kind == SearchSizeKind.Logical
                        ? observation.LogicalBytes
                        : observation.AllocatedBytes
                    : null;
            points.Add(
                new(
                    result.Scans[index].CompletedUtc,
                    bytes,
                    observation.State,
                    observation.State == CapacityObservationState.NotObserved
                        ? null
                        : PathOf(result, observation)
                )
            );
        }
        return points.MoveToImmutable();
    }

    // The scan's own parent and name, with the ancestors named as the result last saw them. A parent
    // the result does not know leaves the path open at its start.
    private static string PathOf(HistorySearchResult result, SearchSizeObservation observation)
    {
        string name = observation.Name ?? "";
        if (observation.Parent is not { } parent || parent == result.Root)
            return name;
        return result.Folder(parent) is null ? "…\\" + name : result.PathOf(parent) + "\\" + name;
    }

    /// <summary>The visible range and ticks for sizes between <paramref name="lowest"/> and
    /// <paramref name="highest"/>. The range is fitted to the sizes instead of starting at zero:
    /// the sizes with a sixteenth of their spread on each side, so that they fill about nine
    /// tenths of it. Sizes that do not differ at all get a range a thousandth of the largest wide,
    /// around them. The ticks are multiples of 1, 2 or 5 times a power of ten of the unit (a
    /// binary unit chosen by the largest size), the finest of which gives at most seven ticks in
    /// the range. They are not the ends of the range. Sizes beyond any real volume (more than 2^110
    /// units of 1024) are capped at the largest value instead of overflowing.</summary>
    public static SizeAxisScale ScaleFor(UInt128 lowest, UInt128 highest)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lowest, highest);
        UInt128 unit = 1;
        while (unit < UInt128.One << 110 && unit << 10 <= highest)
            unit <<= 10;
        BigInteger low = (BigInteger)lowest;
        BigInteger high = (BigInteger)highest;
        BigInteger minimum = high / 1000;
        BigInteger widening = BigInteger.Max(minimum - (high - low), 0) / 2;
        BigInteger margin = BigInteger.Max(high - low, minimum) / 16;
        BigInteger from = BigInteger.Max(low - widening - margin, 0);
        BigInteger to = high + widening + margin;
        if (to == from)
            to += 1;
        // Steps that are whole bytes first, so that ticks do not drift from their labels; a range
        // narrower than any of them (a few bytes) takes the nearest step instead.
        return FindStep(unit, from, to, wholeOnly: true)
            ?? FindStep(unit, from, to, wholeOnly: false)
            ?? throw new InvalidOperationException("No axis step fits the range.");
    }

    private static SizeAxisScale? FindStep(
        UInt128 unit,
        BigInteger from,
        BigInteger to,
        bool wholeOnly
    )
    {
        int digits = 0;
        for (BigInteger magnitude = 1; ; magnitude *= 10, digits++)
            foreach (int factor in new[] { 1, 2, 5 })
            {
                // A step is a millionth of the unit times the factor.
                BigInteger scaled = (BigInteger)unit * factor * magnitude;
                BigInteger step = scaled / 1_000_000;
                if (step == 0 || (wholeOnly && step < 1000 && scaled % 1_000_000 != 0))
                    continue;
                // Beyond the range no larger step has a tick in it either.
                if (from > 0 && step > to)
                    return null;
                BigInteger ticks = to / step - (from + step - 1) / step + 1;
                if (ticks >= 1 && ticks <= 7)
                    return new(unit, Clamp(from), Clamp(step), Clamp(to), Math.Max(0, 6 - digits));
            }
    }

    private static UInt128 Clamp(BigInteger value) =>
        value > (BigInteger)UInt128.MaxValue ? UInt128.MaxValue : (UInt128)value;
}
