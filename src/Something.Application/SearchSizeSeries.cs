using System.Collections.Immutable;
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

/// <summary>Where the ticks of a size axis fall: multiples of <see cref="Step"/> up to
/// <see cref="Top"/>, labeled in multiples of <see cref="Unit"/>.</summary>
public readonly record struct SizeAxisScale(UInt128 Unit, UInt128 Step, UInt128 Top)
{
    /// <summary>The tick values from zero to <see cref="Top"/>.</summary>
    public IEnumerable<UInt128> Ticks()
    {
        for (UInt128 tick = 0; ; tick += Step)
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
    // Fractions of the unit in eighths: a quarter, a half, then doubling up to 512 units.
    private static readonly int[] StepEighths =
    [
        2,
        4,
        8,
        16,
        32,
        64,
        128,
        256,
        512,
        1024,
        2048,
        4096,
    ];

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

    /// <summary>The ticks for sizes up to <paramref name="maximum"/>: binary units and steps that are
    /// halves, quarters or doubles of them, so that labels read 0.25, 0.5, 4 or 512 rather than
    /// 13.97. At most six steps; sizes beyond any real volume (more than 2^110 units of 1024) may
    /// need more, and are capped at the largest value instead of overflowing.</summary>
    public static SizeAxisScale ScaleFor(UInt128 maximum)
    {
        UInt128 unit = 1;
        while (unit < UInt128.One << 110 && unit << 10 <= maximum)
            unit <<= 10;
        UInt128 step = 1;
        foreach (int eighths in StepEighths)
        {
            // Dividing first keeps the product in range for the largest units.
            step = unit >= 8 ? unit / 8 * (UInt128)eighths : unit * (UInt128)eighths / 8;
            if (step == 0)
                continue;
            UInt128 steps = maximum / step + (maximum % step == 0 ? 0u : 1u);
            if (steps <= 6)
                return new(unit, step, UInt128.Max(steps, 1) * step);
        }
        UInt128 count = maximum / step + 1;
        return new(unit, step, count > UInt128.MaxValue / step ? UInt128.MaxValue : count * step);
    }
}
