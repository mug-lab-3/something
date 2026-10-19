using System.Diagnostics;

namespace Something.Storage;

public enum ScanWorkStage
{
    CollectingFolders,
    BuildingHierarchy,
    AggregatingFiles,
    RollingUpFolders,
    ValidatingAggregation,
    WritingFiles,
    WritingFolders,
    WritingFolderNames,
    FlushingRows,
    ValidatingRelationships,
    BuildingFolderList,
    Committing,
    MaintainingHistory,
}

/// <summary>Fraction applies only to this stage, not elapsed time or the entire scan.
/// Unknown totals (native SQL and commit) remain indeterminate.</summary>
public readonly record struct ScanWorkProgress(ScanWorkStage Stage, long Completed, long? Total)
{
    public int RetryAttempt { get; init; }
    public ulong? MemoryLimitBytes { get; init; }

    /// <summary>True only after memory-only attempts failed and the engine may offload to disk.</summary>
    public bool SpillingToDisk { get; init; }
    public double? Fraction =>
        Total is { } total
            ? total == 0
                ? 1
                : Math.Clamp((double)Completed / total, 0, 1)
            : null;
}

/// <summary>Synchronous worker callback; no timers, DB queries or per-record UI dispatch.
/// Start/end are always reported. Intermediate updates are limited to four per second.</summary>
internal sealed class ScanWorkReporter(Action<ScanWorkProgress>? callback)
{
    private ScanWorkStage stage;
    private long? total;
    private long lastReport;

    internal void Start(ScanWorkStage next, long? count = null)
    {
        stage = next;
        total = count;
        Emit(0);
    }

    internal void Advance(long completed)
    {
        if (callback is null)
            return;
        if (completed == total || Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 250)
            Emit(completed);
    }

    private void Emit(long completed)
    {
        if (callback is null)
            return;
        lastReport = Stopwatch.GetTimestamp();
        callback(new(stage, completed, total));
    }
}
