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

/// <summary>The visible range of a size axis and where its ticks fall: multiples of
/// <see cref="Step"/> from <see cref="Bottom"/> to <see cref="Top"/>, labeled in multiples of
/// <see cref="Unit"/>.</summary>
public readonly record struct SizeAxisScale(UInt128 Unit, UInt128 Bottom, UInt128 Step, UInt128 Top)
{
    /// <summary>The tick values from <see cref="Bottom"/> to <see cref="Top"/>.</summary>
    public IEnumerable<UInt128> Ticks()
    {
        for (UInt128 tick = Bottom; ; tick += Step)
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
    /// <paramref name="highest"/>. The range is fitted to the sizes instead of starting at zero,
    /// with some room around them, and is at least a tenth of the largest size wide so that sizes
    /// which hardly differ do not look like a large change. The step is 1, 2 or 5 times a power of
    /// ten of the unit (a binary unit chosen by the largest size), giving at most six steps. It
    /// reaches zero by itself when the sizes are close to it. Sizes beyond any real volume (more
    /// than 2^110 units of 1024) are capped at the largest value instead of overflowing.</summary>
    public static SizeAxisScale ScaleFor(UInt128 lowest, UInt128 highest)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lowest, highest);
        UInt128 unit = 1;
        while (unit < UInt128.One << 110 && unit << 10 <= highest)
            unit <<= 10;
        BigInteger low = (BigInteger)lowest;
        BigInteger high = (BigInteger)highest;
        // Sizes closer together than a tenth of the largest are spread evenly around their middle.
        BigInteger minimum = high / 10;
        BigInteger widening = BigInteger.Max(minimum - (high - low), 0) / 2;
        BigInteger margin = BigInteger.Max(high - low, minimum) / 8;
        BigInteger from = BigInteger.Max(low - widening - margin, 0);
        BigInteger to = high + widening + margin;
        for (BigInteger magnitude = 1; ; magnitude *= 10)
            foreach (int factor in new[] { 1, 2, 5 })
            {
                // A step is a thousandth of the unit times the factor, and a whole number of bytes.
                BigInteger step = (BigInteger)unit * factor * magnitude / 1000;
                if (step == 0)
                    continue;
                BigInteger bottom = from / step * step;
                BigInteger top = (to + step - 1) / step * step;
                if (top == bottom)
                    top += step;
                if ((top - bottom) / step <= 6)
                    return new(unit, Clamp(bottom), Clamp(step), Clamp(top));
            }
    }

    private static UInt128 Clamp(BigInteger value) =>
        value > (BigInteger)UInt128.MaxValue ? UInt128.MaxValue : (UInt128)value;
}
