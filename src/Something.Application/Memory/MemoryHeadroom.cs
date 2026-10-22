namespace Something.Application.Memory;

/// <summary>Memory available on this machine, sampled from the OS.</summary>
public sealed record AvailableMemory(
    ulong TotalPhysicalBytes,
    ulong PhysicalBytes,
    ulong CommitBytes,
    ulong VirtualBytes
);

/// <summary>The one memory policy for work that competes for the machine: what may be used now
/// and what stays for the OS. Budgets follow the machine's availability; engine-specific limits
/// are derived from it by the storage layer.</summary>
public static class MemoryHeadroom
{
    private const ulong GiB = 1UL << 30;

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
}

/// <summary>A value chosen from the available memory and the bytes it holds in the allowance.</summary>
public readonly record struct MemoryChoice<T>(T Value, ulong Bytes);

/// <summary>The memory one operation may hold, shared with the scans and searches of other
/// drives. The storage layer decides how much its engine needs; this only arbitrates.</summary>
public interface IMemoryAllowance
{
    /// <summary>Lets <paramref name="decide"/> choose a value from the memory available to this
    /// operation: the machine's availability less every other holder, or null when unknown. The
    /// bytes of the choice are held atomically, so two operations never take the same headroom;
    /// they raise the operation's hold and never lower it. <paramref name="decide"/> runs under
    /// the arbiter's lock and must not wait.</summary>
    T Charge<T>(Func<AvailableMemory?, MemoryChoice<T>> decide);

    /// <summary>Holds at least <paramref name="bytes"/> for this operation.</summary>
    void Hold(ulong bytes);
}

/// <summary>An allowance for work that runs alone: it reads the current availability and holds
/// nothing, so it never competes with other holders.</summary>
public sealed class UnsharedMemoryAllowance(Func<AvailableMemory?> readAvailable) : IMemoryAllowance
{
    public T Charge<T>(Func<AvailableMemory?, MemoryChoice<T>> decide) =>
        decide(MemoryHeadroom.Validate(readAvailable())).Value;

    public void Hold(ulong bytes) { }
}
