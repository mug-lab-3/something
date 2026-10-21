namespace Something.Application.Memory;

public enum DiagnosticDatabasePreference
{
    Automatic,
    Memory,
    File,
}

public enum DatabaseSelectionReason
{
    SufficientHeadroom,
    InsufficientHeadroom,
    MemoryInfoUnavailable,
    ForcedMemory,
    ForcedFile,
}

public sealed record AvailableMemory(
    ulong TotalPhysicalBytes,
    ulong PhysicalBytes,
    ulong CommitBytes,
    ulong VirtualBytes
);

public sealed record DiagnosticDatabaseSelection(
    bool UseMemory,
    DatabaseSelectionReason Reason,
    ulong EstimatedDatabaseBytes,
    ulong RequiredAdditionalBytes,
    ulong ReserveBytes,
    AvailableMemory? Available,
    ulong FileCacheBytes
);

/// <summary>Pure, conservative selection policy. Available values must be sampled after buffering
/// and aggregation, so the existing C# buffer is already charged to the machine.</summary>
public static class DiagnosticDatabaseSelector
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    /// <summary>DuckDB's engine budget includes query workspace and resident indexes, not
    /// just cached file pages. A small new scan can share a much larger history database.</summary>
    public static DiagnosticDatabaseSelection SelectDuckDb(
        ulong estimatedDatabaseBytes,
        AvailableMemory? available,
        DiagnosticDatabasePreference preference = DiagnosticDatabasePreference.File
    )
    {
        var selection = Select(estimatedDatabaseBytes, available, preference);
        if (selection.UseMemory)
            return selection;
        // FileCacheBytes is the legacy transport field; for DuckDB it carries memory_limit.
        return selection with { FileCacheBytes = MemoryHeadroom.DuckLimit(selection.Available) };
    }

    public static DiagnosticDatabaseSelection Select(
        ulong estimatedDatabaseBytes,
        AvailableMemory? available,
        DiagnosticDatabasePreference preference = DiagnosticDatabasePreference.Automatic
    )
    {
        if (!Enum.IsDefined(preference))
            throw new ArgumentOutOfRangeException(nameof(preference));
        if (estimatedDatabaseBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedDatabaseBytes));
        available = MemoryHeadroom.Validate(available);
        ulong reserve = MemoryHeadroom.OsReserve(available);
        // Database pages plus native overhead, future ranking allocations and OS headroom.
        UInt128 required = (UInt128)estimatedDatabaseBytes * 2 + reserve;
        bool fits = available is not null && required <= MemoryHeadroom.Headroom(available);
        var reason = preference switch
        {
            DiagnosticDatabasePreference.Memory => DatabaseSelectionReason.ForcedMemory,
            DiagnosticDatabasePreference.File => DatabaseSelectionReason.ForcedFile,
            _ when available is null => DatabaseSelectionReason.MemoryInfoUnavailable,
            _ when fits => DatabaseSelectionReason.SufficientHeadroom,
            _ => DatabaseSelectionReason.InsufficientHeadroom,
        };
        bool useMemory =
            preference == DiagnosticDatabasePreference.Memory
            || preference == DiagnosticDatabasePreference.Automatic && fits;
        ulong cacheBytes = 0;
        if (!useMemory)
        {
            const ulong MiB = 1024 * 1024;
            // At most a quarter of post-reserve headroom: leave space for native cache
            // overhead and later ranking/diagnostic allocations. SQLite allocates on demand.
            cacheBytes = available is null
                ? 64 * MiB
                : MemoryHeadroom.Align(
                    Math.Clamp(
                        Math.Min(MemoryHeadroom.Surplus(available) / 4, estimatedDatabaseBytes),
                        64 * MiB,
                        2 * GiB
                    )
                );
        }
        return new(
            useMemory,
            reason,
            estimatedDatabaseBytes,
            required > ulong.MaxValue ? ulong.MaxValue : (ulong)required,
            reserve,
            available,
            cacheBytes
        );
    }
}
