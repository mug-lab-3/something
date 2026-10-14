namespace Something.Storage;

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
        ulong limit = GiB;
        if (selection.Available is { } memory)
        {
            ulong headroom = Math.Min(
                memory.PhysicalBytes,
                Math.Min(memory.CommitBytes, memory.VirtualBytes)
            );
            ulong surplus =
                headroom > selection.ReserveBytes ? headroom - selection.ReserveBytes : 0;
            // Reserve matching headroom for native allocations outside memory_limit.
            // No fixed GiB ceiling: larger PCs can use their available workspace.
            limit = Math.Clamp(surplus / 2, GiB, DuckDatabaseBootstrap.MaximumMemoryLimit);
            limit = limit / 16384 * 16384;
        }
        // FileCacheBytes is the legacy transport field; for DuckDB it carries memory_limit.
        return selection with { FileCacheBytes = limit };
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
        if (
            available is { TotalPhysicalBytes: 0 }
            || available is not null && available.PhysicalBytes > available.TotalPhysicalBytes
        )
            available = null;
        ulong reserve = available is null
            ? GiB
            : Math.Clamp(available.TotalPhysicalBytes / 5, GiB, 4 * GiB);
        // Database pages plus native overhead, future ranking allocations and OS headroom.
        UInt128 required = (UInt128)estimatedDatabaseBytes * 2 + reserve;
        bool fits =
            required <= ulong.MaxValue
            && available is not null
            && required <= available.PhysicalBytes
            && required <= available.CommitBytes
            && required <= available.VirtualBytes;
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
            if (available is null)
                cacheBytes = 64 * MiB;
            else
            {
                ulong headroom = Math.Min(
                    available.PhysicalBytes,
                    Math.Min(available.CommitBytes, available.VirtualBytes)
                );
                ulong surplus = headroom > reserve ? headroom - reserve : 0;
                // At most a quarter of post-reserve headroom: leave space for native cache
                // overhead and later ranking/diagnostic allocations. SQLite allocates on demand.
                cacheBytes = Math.Clamp(
                    Math.Min(surplus / 4, estimatedDatabaseBytes),
                    64 * MiB,
                    2 * GiB
                );
                cacheBytes = cacheBytes / 16384 * 16384;
            }
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
