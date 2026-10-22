using Something.Application.Memory;

namespace Something.Storage;

/// <summary>How much memory a DuckDB engine asks the shared allowance for. Limits follow the
/// machine's availability; the floor exists so that a database can open at all, and is never a cap.</summary>
internal static class DuckMemoryPolicy
{
    private const ulong GiB = 1UL << 30;

    /// <summary>DuckDB limits are whole multiples of this many bytes.</summary>
    internal const ulong Granularity = 16384;

    /// <summary>The smallest limit; used when availability is unknown or short.</summary>
    internal const ulong MinimumLimit = GiB;

    /// <summary>Native limits are signed byte counts. A representation bound, not a budget.</summary>
    internal const ulong MaximumLimit = (ulong)long.MaxValue / Granularity * Granularity;

    /// <summary>Half of the surplus, so native allocations outside the limit keep matching room.
    /// No fixed ceiling: larger machines use their available workspace.</summary>
    internal static ulong InitialLimit(AvailableMemory? available) =>
        available is null ? MinimumLimit : Align(Math.Max(Surplus(available) / 2, MinimumLimit));

    /// <summary>The next limit for an engine at <paramref name="current"/>, or null when the
    /// growth does not fit the availability.</summary>
    internal static ulong? Grow(AvailableMemory? available, ulong current)
    {
        if (available is null)
            return null;
        ulong next = Align((UInt128)current + Surplus(available) / 2);
        if (next <= current)
            return null;
        // Availability already charges the live buffer and existing native allocations; hold twice
        // the incremental growth for native overhead, not all existing memory again.
        ulong additional = Charge(next - current);
        return MemoryHeadroom.Fits(available, additional, MemoryHeadroom.OsReserve(available))
            ? next
            : null;
    }

    /// <summary>What a limit holds in the allowance: the engine plus native allocations outside it.</summary>
    internal static ulong Charge(ulong limit) =>
        (ulong)UInt128.Min((UInt128)limit * 2, ulong.MaxValue);

    /// <summary>Rounds down to the granularity, within the native maximum.</summary>
    internal static ulong Align(UInt128 bytes) =>
        (ulong)UInt128.Min(bytes, MaximumLimit) / Granularity * Granularity;

    private static ulong Surplus(AvailableMemory available) => MemoryHeadroom.Surplus(available);

    /// <summary>The limit and growth a database operation obtains from <paramref name="allowance"/>.</summary>
    internal static (ulong Limit, Func<ulong, ulong?> Grow, Action<ulong> Account) Acquire(
        IMemoryAllowance allowance
    )
    {
        ArgumentNullException.ThrowIfNull(allowance);
        ulong limit = allowance.Charge<ulong>(available =>
        {
            ulong value = InitialLimit(available);
            return new(value, Charge(value));
        });
        return (
            limit,
            current =>
                allowance.Charge<ulong?>(available =>
                    Grow(available, current) is { } next ? new(next, Charge(next)) : new(null, 0)
                ),
            chosen => allowance.Hold(Charge(chosen))
        );
    }
}
