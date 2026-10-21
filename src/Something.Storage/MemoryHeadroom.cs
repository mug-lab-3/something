namespace Something.Storage;

/// <summary>The one memory policy for database work: what may be used now, what stays for the OS,
/// and how a DuckDB limit is derived. Budgets follow the machine's availability; the floor exists
/// so that a database can open at all, and is never a cap.</summary>
public static class MemoryHeadroom
{
    private const ulong GiB = 1UL << 30;

    /// <summary>DuckDB limits are whole multiples of this many bytes.</summary>
    public const ulong Granularity = 16384;

    /// <summary>The smallest DuckDB limit; used when availability is unknown or short.</summary>
    public const ulong MinimumDuckLimit = GiB;

    /// <summary>Native limits are signed byte counts. A representation bound, not a budget.</summary>
    public const ulong MaximumDuckLimit = (ulong)long.MaxValue / Granularity * Granularity;

    /// <summary>Null for missing or inconsistent samples, which must not be trusted.</summary>
    public static AvailableMemory? Validate(AvailableMemory? memory) =>
        memory is null
        || memory.TotalPhysicalBytes == 0
        || memory.PhysicalBytes > memory.TotalPhysicalBytes
            ? null
            : memory;

    /// <summary>Left for the OS and other processes: 20% of physical RAM within 1–4 GiB.</summary>
    public static ulong OsReserve(AvailableMemory? memory) =>
        memory is null ? GiB : Math.Clamp(memory.TotalPhysicalBytes / 5, GiB, 4 * GiB);

    /// <summary>The scarcest of physical, commit and virtual availability.</summary>
    public static ulong Headroom(AvailableMemory memory) =>
        Math.Min(memory.PhysicalBytes, Math.Min(memory.CommitBytes, memory.VirtualBytes));

    /// <summary>Headroom beyond the OS reserve.</summary>
    public static ulong Surplus(AvailableMemory memory)
    {
        ulong headroom = Headroom(memory),
            reserve = OsReserve(memory);
        return headroom > reserve ? headroom - reserve : 0;
    }

    /// <summary>Whether <paramref name="bytes"/> plus <paramref name="reserve"/> fits every kind
    /// of availability. Unknown availability never fits.</summary>
    public static bool Fits(AvailableMemory? memory, ulong bytes, ulong reserve) =>
        memory is not null && (UInt128)bytes + reserve <= Headroom(memory);

    /// <summary>Half of the surplus, so native allocations outside the limit keep matching room.
    /// No fixed ceiling: larger machines use their available workspace.</summary>
    public static ulong DuckLimit(AvailableMemory? memory) =>
        memory is null ? MinimumDuckLimit : Align(Math.Max(Surplus(memory) / 2, MinimumDuckLimit));

    /// <summary>Rounds down to the granularity, within the native maximum.</summary>
    public static ulong Align(UInt128 bytes) =>
        (ulong)UInt128.Min(bytes, MaximumDuckLimit) / Granularity * Granularity;
}
